namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorWakeReason
{
    Unknown,
    AttemptExit,
    DispatchExit,
    OperatorIntent,
    StopFile
}

internal interface IConductorAttemptExitWakeSignal
{
    void UpdateTrackedAttemptExitArtifacts(IReadOnlyCollection<string> attemptExitCodePaths);

    ConductorWakeReason LastWakeReason { get; }
}

internal sealed partial class FileSystemWatcherConductorWakeSignal
{
    private HashSet<string> _trackedAttemptExitPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _signaledAttemptExitPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _attemptWatchers = [];
    private int _lastWakeReason;
    private int _attemptWatcherWarningEmitted;

    public ConductorWakeReason LastWakeReason => (ConductorWakeReason)Volatile.Read(ref _lastWakeReason);

    public void UpdateTrackedAttemptExitArtifacts(IReadOnlyCollection<string> attemptExitCodePaths)
    {
        var tracked = attemptExitCodePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_trackedGate)
        {
            _trackedAttemptExitPaths = tracked;
            _signaledAttemptExitPaths.IntersectWith(tracked);
        }

        DisposeAttemptWatchers();
        foreach (var directory in tracked.Select(Path.GetDirectoryName)
                     .Where(directory => directory is not null && Directory.Exists(directory))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(directory!, "*.exit.txt")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
                };
                watcher.Created += (_, args) => SignalIfTrackedAttemptExit(args.FullPath);
                watcher.Changed += (_, args) => SignalIfTrackedAttemptExit(args.FullPath);
                watcher.Renamed += (_, args) => SignalIfTrackedAttemptExit(args.FullPath);
                watcher.Error += (_, args) => WarnAttemptWatcher(args.GetException());
                watcher.EnableRaisingEvents = true;
                _attemptWatchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                WarnAttemptWatcher(ex);
            }
        }
    }

    private void SignalIfTrackedAttemptExit(string path)
    {
        var normalized = NormalizePath(path);
        lock (_trackedGate)
        {
            if (!_trackedAttemptExitPaths.Contains(normalized) || !_signaledAttemptExitPaths.Add(normalized))
                return;
        }

        Volatile.Write(ref _lastWakeReason, (int)ConductorWakeReason.AttemptExit);
        Signal();
    }

    private bool TryConsumeExistingAttemptExit()
    {
        lock (_trackedGate)
        {
            foreach (var path in _trackedAttemptExitPaths)
            {
                if (_signaledAttemptExitPaths.Contains(path) || !File.Exists(path))
                    continue;

                _signaledAttemptExitPaths.Add(path);
                Volatile.Write(ref _lastWakeReason, (int)ConductorWakeReason.AttemptExit);
                return true;
            }
        }

        return false;
    }

    private void WarnAttemptWatcher(Exception ex)
    {
        if (Interlocked.Exchange(ref _attemptWatcherWarningEmitted, 1) != 0)
            return;

        try
        {
            _warn($"[conduct --loop --watch] Warning: attempt exit-file watcher unavailable; continuing with timed polling. {ex.Message}");
        }
        catch
        {
            // A caller-supplied warn delegate must never crash the watcher callback thread.
        }
    }

    private void DisposeAttemptWatchers()
    {
        foreach (var watcher in _attemptWatchers)
            watcher.Dispose();
        _attemptWatchers.Clear();
    }
}
