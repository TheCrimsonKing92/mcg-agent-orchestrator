using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorWakeSignal : IDisposable
{
    void UpdateTrackedExitArtifacts(IReadOnlyCollection<string> exitCodePaths);

    bool Wait(TimeSpan timeout);
}

internal sealed class FileSystemWatcherConductorWakeSignal : IConductorWakeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Action<string> _warn;
    private readonly FileSystemWatcher? _watcher;
    private readonly string _watchDirectory;
    private readonly object _trackedGate = new();
    private HashSet<string> _trackedExitCodePaths = new(StringComparer.OrdinalIgnoreCase);
    private int _signaled;
    private int _watcherFailed;
    private int _scanWarningEmitted;

    public FileSystemWatcherConductorWakeSignal(string exitDirectory, Action<string>? warn = null)
    {
        _warn = warn ?? (message => Console.Error.WriteLine(message));
        _watchDirectory = Path.GetFullPath(exitDirectory);

        try
        {
            Directory.CreateDirectory(_watchDirectory);
            _watcher = new FileSystemWatcher(_watchDirectory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
            };
            _watcher.Filters.Add("*.exit.txt");
            _watcher.Filters.Add($"*{SqliteOperatorIntentStore.WakeFileSuffix}");
            _watcher.Created += (_, args) => SignalIfTracked(args.FullPath);
            _watcher.Changed += (_, args) => SignalIfTracked(args.FullPath);
            _watcher.Renamed += (_, args) => SignalIfTracked(args.FullPath);
            _watcher.Error += (_, args) =>
            {
                Interlocked.Exchange(ref _watcherFailed, 1);
                _warn($"[conduct --loop --watch] Warning: dispatch exit-file watcher failed; continuing with timed polling. {args.GetException().Message}");
                Signal();
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _warn($"[conduct --loop --watch] Warning: dispatch exit-file watcher unavailable; continuing with timed polling. {ex.Message}");
        }
    }

    public void UpdateTrackedExitArtifacts(IReadOnlyCollection<string> exitCodePaths)
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in exitCodePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            tracked.Add(NormalizePath(path));
        }

        lock (_trackedGate)
        {
            _trackedExitCodePaths = tracked;
        }

        DrainStaleSignals();
        if (HasExistingTrackedExitArtifact())
        {
            Signal();
        }
    }

    public bool Wait(TimeSpan timeout)
    {
        if (HasExistingTrackedExitArtifact())
        {
            return true;
        }

        if (_watcher is null || Volatile.Read(ref _watcherFailed) != 0)
        {
            Thread.Sleep(timeout);
            return HasExistingTrackedExitArtifact();
        }

        if (!_signal.Wait(timeout))
        {
            return HasExistingTrackedExitArtifact();
        }

        Interlocked.Exchange(ref _signaled, 0);
        return true;
    }

    private void SignalIfTracked(string path)
    {
        if (!IsTracked(path))
        {
            return;
        }

        Signal();
    }

    private bool IsTracked(string path)
    {
        if (path.EndsWith(SqliteOperatorIntentStore.WakeFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedPath = NormalizePath(path);
        lock (_trackedGate)
        {
            return _trackedExitCodePaths.Contains(normalizedPath);
        }
    }

    private bool HasExistingTrackedExitArtifact()
    {
        string[] paths;
        lock (_trackedGate)
        {
            paths = _trackedExitCodePaths.ToArray();
        }

        if (paths.Any(File.Exists))
        {
            return true;
        }

        if (!Directory.Exists(_watchDirectory))
        {
            return false;
        }

        try
        {
            var wakePaths = Directory.EnumerateFiles(
                    _watchDirectory,
                    $"*{SqliteOperatorIntentStore.WakeFileSuffix}",
                    SearchOption.TopDirectoryOnly)
                .ToArray();
            if (wakePaths.Length == 0)
            {
                return false;
            }

            // Wake files are edge notifications only; the durable intent remains in SQLite.
            // Consume every observed edge so an out-of-scope or invalid goal cannot hot-spin --watch.
            foreach (var wakePath in wakePaths)
            {
                try
                {
                    File.Delete(wakePath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Exchange(ref _scanWarningEmitted, 1) == 0)
            {
                _warn($"[conduct --loop --watch] Warning: wake-file scan failed; continuing with timed polling. {ex.Message}");
            }

            return false;
        }
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _signaled, 1) != 0)
        {
            return;
        }

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void DrainStaleSignals()
    {
        while (_signal.Wait(0))
        {
        }

        Interlocked.Exchange(ref _signaled, 0);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _signal.Dispose();
    }
}
