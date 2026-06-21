namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorWakeSignal : IDisposable
{
    bool Wait(TimeSpan timeout);
}

internal sealed class FileSystemWatcherConductorWakeSignal : IConductorWakeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Action<string> _warn;
    private readonly FileSystemWatcher? _watcher;
    private int _signaled;

    public FileSystemWatcherConductorWakeSignal(string exitDirectory, Action<string>? warn = null)
    {
        _warn = warn ?? (message => Console.Error.WriteLine(message));

        try
        {
            Directory.CreateDirectory(exitDirectory);
            _watcher = new FileSystemWatcher(exitDirectory, "*.exit.txt")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _watcher.Created += (_, _) => Signal();
            _watcher.Error += (_, args) =>
                _warn($"[conduct --loop --watch] Warning: dispatch exit-file watcher failed; continuing with timed polling. {args.GetException().Message}");
        }
        catch (Exception ex)
        {
            _warn($"[conduct --loop --watch] Warning: dispatch exit-file watcher unavailable; continuing with timed polling. {ex.Message}");
        }
    }

    public bool Wait(TimeSpan timeout)
    {
        if (_watcher is null)
        {
            Thread.Sleep(timeout);
            return false;
        }

        if (!_signal.Wait(timeout))
        {
            return false;
        }

        Interlocked.Exchange(ref _signaled, 0);
        return true;
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

    public void Dispose()
    {
        _watcher?.Dispose();
        _signal.Dispose();
    }
}
