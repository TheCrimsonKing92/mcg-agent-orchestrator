internal static class MarkerFileProbe
{
    internal static async Task WaitAsync(
        string markerPath,
        TimeSpan bound,
        Func<ChildState>? childState = null)
    {
        var fullPath = Path.GetFullPath(markerPath);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? watcherError = null;

        void Probe()
        {
            if (File.Exists(fullPath))
                completion.TrySetResult();
        }

        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        watcher.Created += (_, _) => Probe();
        watcher.Renamed += (_, _) => Probe();
        watcher.Changed += (_, _) => Probe();
        watcher.Error += (_, error) =>
        {
            Volatile.Write(ref watcherError, error.GetException().Message);
            Probe();
        };
        watcher.EnableRaisingEvents = true;
        Probe();
        await using var timer = new Timer(_ => Probe(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10))
            .ConfigureAwait(false);

        try
        {
            await completion.Task.WaitAsync(bound).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            string observedChild;
            try
            {
                observedChild = (childState?.Invoke() ?? ChildState.NotStarted).ToString();
            }
            catch (Exception stateError)
            {
                observedChild = $"unavailable ({stateError.Message})";
            }

            throw new TimeoutException(
                $"Hang guard: descendant PID marker file '{fullPath}' did not appear within {bound}. " +
                $"markerExists={File.Exists(fullPath)}; child={observedChild}; " +
                $"watcherError={Volatile.Read(ref watcherError) ?? "none"}.", error);
        }
    }

    internal readonly record struct ChildState
    {
        private readonly bool? _hasExited;
        private readonly int? _exitCode;

        private ChildState(bool hasExited, int? exitCode)
        {
            _hasExited = hasExited;
            _exitCode = exitCode;
        }

        internal static ChildState NotStarted => default;
        internal static ChildState Running => new(false, null);
        internal static ChildState Exited(int exitCode) => new(true, exitCode);

        public override string ToString() => _hasExited switch
        {
            null => "not started",
            false => "running",
            true => $"exited exitCode={_exitCode}"
        };
    }
}
