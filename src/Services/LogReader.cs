using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using k8s;
using Vigilante.Constants;
using Vigilante.Models;
using Vigilante.Services.Interfaces;

namespace Vigilante.Services;

/// <summary>
/// Reads logs from Kubernetes pods (Qdrant) and from the Vigilante service pod
/// </summary>
public class LogReader(
    IKubernetes? kubernetes, 
    IKubernetesManager? kubernetesManager,
    ILogger<LogReader> logger, 
    IWebHostEnvironment env) : ILogReader
{
    private const int DefaultLimit = 200;
    private const string ContinuationSeparator = "|";
    private static readonly Regex VigilanteLevelRegex = new(@"^\[[^\]]+\s+([A-Z]{3,5})\]", RegexOptions.Compiled);
    private static readonly Regex GenericLeadingLevelRegex = new(@"^\s*([A-Z]{3,5})\b", RegexOptions.Compiled);

    public async Task<LogPage> GetQdrantPodLogsAsync(string podName, LogQuery query, CancellationToken cancellationToken)
    {
        var ns = string.IsNullOrWhiteSpace(query.Namespace) ? KubernetesConstants.DefaultNamespace : query.Namespace!;
        return await GetPodLogsAsync(podName, ns, query, cancellationToken);
    }

    public async Task<LogPage> GetServiceLogsAsync(LogQuery query, CancellationToken cancellationToken)
    {
        // When running in cluster, we can read our own pod logs via Kubernetes
        if (kubernetes != null)
        {
            var podName = Environment.GetEnvironmentVariable("HOSTNAME");
            var ns = kubernetesManager?.GetCurrentNamespace() ?? KubernetesConstants.DefaultNamespace;
            if (!string.IsNullOrWhiteSpace(podName))
            {
                return await GetPodLogsAsync(podName, ns, query, cancellationToken, source: "vigilante");
            }
        }

        // Fallback: try local file logs (Serilog rolling file)
        var fileResponse = await ReadLocalLogsAsync(query, cancellationToken);
        if (fileResponse != null)
        {
            return fileResponse;
        }

        return Failed("Service logs are not available (no Kubernetes client and no local log file)");
    }

    private async Task<LogPage> GetPodLogsAsync(
        string podName,
        string podNamespace,
        LogQuery query,
        CancellationToken cancellationToken,
        string? source = null)
    {
        if (kubernetes == null)
        {
            return Failed(KubernetesConstants.KubernetesClientNotAvailableMessage);
        }

        var core = kubernetes.CoreV1;
        var limit = query.Limit <= 0 ? DefaultLimit : Math.Min(query.Limit, 1000);
        var cursor = ParseContinuation(query.Continuation);
        var fetchLimit = limit + 1; // fetch one extra to detect truncation
        var sinceSeconds = cursor == null
            ? (query.Continuation != null ? 1 : null) // ensure continuation drives a lower-bound even if parsing fails
            : (int?)Math.Max(0, (int)(DateTime.UtcNow - cursor.Value.ToUniversalTime()).TotalSeconds);

        try
        {
            var response = await core.ReadNamespacedPodLogWithHttpMessagesAsync(
                name: podName,
                namespaceParameter: podNamespace,
                container: null,
                follow: false,
                insecureSkipTLSVerifyBackend: null,
                limitBytes: null,
                pretty: null,
                previous: null,
                sinceSeconds: sinceSeconds,
                stream: null,
                tailLines: fetchLimit,
                timestamps: true,
                customHeaders: null,
                cancellationToken: cancellationToken);

            if (response.Body == null)
            {
                return Failed($"Failed to read logs for pod {podName}: empty response body");
            }

            string raw;
            using (var reader = new StreamReader(response.Body, Encoding.UTF8))
            {
                raw = await reader.ReadToEndAsync(cancellationToken);
            }

            var entries = ParseLogs(raw, source ?? podName);
            if (cursor is not null)
            {
                entries = entries.Where(e => e.Timestamp > cursor.Value).ToList();
            }
            entries = ApplyFilters(entries, query);

            var pageEntries = entries.Take(limit).ToList();
            var truncated = entries.Count > limit;
            var next = truncated ? BuildContinuationToken(pageEntries.LastOrDefault()) : null;

            return new LogPage(true, null, pageEntries, next, truncated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read logs for pod {Pod}", podName);
            return Failed($"Failed to read logs for pod {podName}: {ex.Message}");
        }
    }

    private async Task<LogPage?> ReadLocalLogsAsync(LogQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var logDir = Path.Combine(env.ContentRootPath, "logs");
            var file = Directory.GetFiles(logDir, "*.log").OrderByDescending(f => f).FirstOrDefault();
            if (file == null)
            {
                return null;
            }

            var lines = await File.ReadAllLinesAsync(file, cancellationToken);
            var limit = query.Limit <= 0 ? DefaultLimit : Math.Min(query.Limit, 1000);
            var cursor = ParseContinuation(query.Continuation);

            var parsed = ParseLogs(string.Join('\n', lines), "vigilante");
            if (cursor is not null)
            {
                parsed = parsed.Where(e => e.Timestamp > cursor.Value).ToList();
            }
            parsed = ApplyFilters(parsed, query);

            var ordered = parsed.OrderBy(e => e.Timestamp).ToList();
            var entries = ordered.TakeLast(limit + 1).ToList();
            var truncated = ordered.Count > limit;
            if (truncated)
            {
                entries = entries.TakeLast(limit).ToList();
            }

            var next = truncated ? BuildContinuationToken(entries.LastOrDefault()) : null;

            return new LogPage(true, null, entries, next, truncated);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read local log file");
            return null;
        }
    }

    private DateTime? ParseContinuation(string? continuation)
    {
        if (string.IsNullOrWhiteSpace(continuation))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(continuation));
            var parts = decoded.Split(ContinuationSeparator);
            if (parts.Length >= 1 && DateTime.TryParse(parts[0], null, DateTimeStyles.RoundtripKind, out var ts))
            {
                return ts;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse continuation token");
        }

        return null;
    }

    private string? BuildContinuationToken(LogEntry? last)
    {
        if (last == null)
        {
            return null;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.Timestamp:o}{ContinuationSeparator}{last.Source}"));
    }


    private static LogPage Failed(string message) => new(false, message, Array.Empty<LogEntry>(), null, false);

    private static List<LogEntry> ApplyFilters(List<LogEntry> entries, LogQuery query)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        IEnumerable<LogEntry> filtered = entries;

        var levelsFilter = query.Levels;
        if (levelsFilter is not null && levelsFilter.Value != LogLevelFilter.All && levelsFilter.Value != LogLevelFilter.None)
        {
            filtered = filtered.Where(entry =>
            {
                var level = TryExtractLevel(entry.Message);
                return level is not null && levelsFilter.Value.HasFlag(level.Value);
            });
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var searchText = query.SearchText.Trim();
            filtered = filtered.Where(entry => entry.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        return filtered.ToList();
    }

    private static LogLevelFilter? TryExtractLevel(string message)
    {
        var vigilanteMatch = VigilanteLevelRegex.Match(message);
        if (vigilanteMatch.Success)
        {
            return NormalizeLevel(vigilanteMatch.Groups[1].Value);
        }

        var qdrantMatch = GenericLeadingLevelRegex.Match(message);
        if (qdrantMatch.Success)
        {
            return NormalizeLevel(qdrantMatch.Groups[1].Value);
        }

        return null;
    }

    private static LogLevelFilter? NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return null;
        }

        return level.Trim().ToUpperInvariant() switch
        {
            "TRC" => LogLevelFilter.Trace,
            "TRACE" => LogLevelFilter.Trace,
            "DBG" => LogLevelFilter.Debug,
            "DEBUG" => LogLevelFilter.Debug,
            "INF" => LogLevelFilter.Info,
            "INFO" => LogLevelFilter.Info,
            "WRN" => LogLevelFilter.Warn,
            "WARN" => LogLevelFilter.Warn,
            "ERR" => LogLevelFilter.Error,
            "ERROR" => LogLevelFilter.Error,
            "FTL" => LogLevelFilter.Fatal,
            "FATAL" => LogLevelFilter.Fatal,
            _ => null
        };
    }

    private List<LogEntry> ParseLogs(string raw, string source)
    {
        var result = new List<LogEntry>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var firstSpace = line.IndexOf(' ');
            if (firstSpace <= 0)
            {
                continue;
            }

            var tsPart = line[..firstSpace];
            if (!DateTime.TryParse(tsPart, null, DateTimeStyles.RoundtripKind, out var ts))
            {
                continue;
            }

            var messagePart = line[(firstSpace + 1)..];
            result.Add(new LogEntry(ts, messagePart, source));
        }

        return result.OrderBy(e => e.Timestamp).ToList();
    }
}
