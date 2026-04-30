namespace Vigilante.Models;

[Flags]
public enum LogLevelFilter
{
    None = 0,
    Trace = 1 << 0,
    Debug = 1 << 1,
    Info = 1 << 2,
    Warn = 1 << 3,
    Error = 1 << 4,
    Fatal = 1 << 5,
    All = Trace | Debug | Info | Warn | Error | Fatal
}

/// <summary>
/// Query parameters for log retrieval at service level.
/// </summary>
public record LogQuery(
    string? Namespace,
    int Limit = 200,
    string? Continuation = null,
    LogLevelFilter? Levels = null,
    string? SearchText = null);

/// <summary>
/// A single log entry as returned by log readers.
/// </summary>
public record LogEntry(DateTime Timestamp, string Message, string Source);

/// <summary>
/// Page of logs with optional continuation token and status flags.
/// </summary>
public record LogPage(bool Success, string? Error, IReadOnlyList<LogEntry> Logs, string? Continuation, bool Truncated);
