namespace Vigilante.Services;

/// <inheritdoc />
public sealed class SnapshotAutomationStatus : Interfaces.ISnapshotAutomationStatus
{
    private readonly object _lock = new();
    private int _runDepth;
    private string? _currentAction;
    private DateTime? _lastRunStartedUtc;
    private DateTime? _lastCompletedUtc;
    private bool? _lastRunSuccess;
    private readonly List<string> _runNotes = [];
    private string? _lastRunSummary;

    public void BeginRun()
    {
        lock (_lock)
        {
            if (_runDepth == 0)
            {
                _lastRunStartedUtc = DateTime.UtcNow;
                _runNotes.Clear();
            }

            _runDepth++;
        }
    }

    public void EndRun(bool success)
    {
        lock (_lock)
        {
            _runDepth = Math.Max(0, _runDepth - 1);
            _currentAction = null;
            if (_runDepth == 0)
            {
                _lastCompletedUtc = DateTime.UtcNow;
                _lastRunSuccess = success;
                if (_runNotes.Count > 0)
                    _lastRunSummary = string.Join(" · ", _runNotes);
                _runNotes.Clear();
            }
        }
    }

    public void AppendRunNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return;
        lock (_lock)
        {
            _runNotes.Add(note.Trim());
        }
    }

    public void SetCurrentAction(string? action)
    {
        lock (_lock)
        {
            _currentAction = action;
        }
    }

    public IReadOnlyDictionary<string, object?> GetDisplayMetadata()
    {
        lock (_lock)
        {
            var running = _runDepth > 0;
            var d = new Dictionary<string, object?>
            {
                ["phase"] = running ? "running" : "idle",
                ["lastRunStartedUtc"] = _lastRunStartedUtc,
                ["lastCompletedUtc"] = _lastCompletedUtc,
                ["lastRunSuccess"] = _lastRunSuccess
            };
            if (!string.IsNullOrEmpty(_currentAction))
                d["CurrentAction"] = _currentAction;
            if (!string.IsNullOrEmpty(_lastRunSummary))
                d["lastRunSummary"] = _lastRunSummary;
            return d;
        }
    }
}
