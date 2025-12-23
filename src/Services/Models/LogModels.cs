namespace Vigilante.Services.Models;

/// <summary>
/// Query parameters for log retrieval at service level.
/// </summary>
public record LogQuery(string? Namespace, int Limit = 200, string? Continuation = null);

/// <summary>
/// A single log entry as returned by log readers.
/// </summary>
public record LogEntry(DateTime Timestamp, string Message, string Source);

/// <summary>
/// Page of logs with optional continuation token and status flags.
/// </summary>
public record LogPage(bool Success, string? Error, IReadOnlyList<LogEntry> Logs, string? Continuation, bool Truncated);
