using System.Diagnostics;
using System.Text.Json;

// Event-driven, read-only observation of one helper's disposable bootstrap output.
internal sealed class WorkerBuildBridgeBootstrapCapture : IDisposable
{
    private readonly WorkerBuildBridgeCandidateBindings _bindings;
    private readonly FileSystemWatcher _watcher;
    private FileSystemWatcher? _scopedWatcher;
    private readonly object _gate = new();
    private readonly string _processRoot;
    private readonly string _bootstrap;
    private readonly object _child;
    private Dictionary<string, WorkerBuildBridgeCandidateBindings.Identity>? _copied;
    private Exception? _watcherFailure;
    private bool _disposed;

    internal WorkerBuildBridgeBootstrapCapture(string fixtureRoot, Process child, WorkerBuildBridgeCandidateBindings bindings)
    {
        _bindings = bindings;
        var temp = Path.GetTempPath();
        if (temp.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
            temp = Path.Combine(local, "Temp");
        _processRoot = Path.Combine(temp, "pt", "goal-manual", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _bootstrap = Path.Combine(_processRoot, "scope-tool", "bin");
        _child = new { pid = child.Id, startTimeUtc = child.StartTime.ToUniversalTime(), image = child.MainModule!.FileName, arguments = child.StartInfo.ArgumentList.ToArray(), bootstrap = _bootstrap };
        _watcher = new FileSystemWatcher(temp)
        {
            IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += (_, args) => { if (args.FullPath.Equals(_processRoot, StringComparison.OrdinalIgnoreCase)) ArmScopedWatcher(); };
        _watcher.Error += (_, args) => { lock (_gate) _watcherFailure = args.GetException(); };
        _watcher.EnableRaisingEvents = true;
        ArmScopedWatcher();
    }

    private void ArmScopedWatcher()
    {
        lock (_gate)
        {
            if (_disposed || _scopedWatcher is not null || !Directory.Exists(_processRoot)) return;
            try
            {
                _scopedWatcher = new FileSystemWatcher(_processRoot)
                {
                    IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                _scopedWatcher.Created += Observe;
                _scopedWatcher.Changed += Observe;
                _scopedWatcher.Renamed += Observe;
                _scopedWatcher.Error += (_, args) => { lock (_gate) _watcherFailure = args.GetException(); };
                _scopedWatcher.EnableRaisingEvents = true;
                _watcher.EnableRaisingEvents = false;
                TryCapture();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _watcherFailure = exception;
            }
        }
    }

    private void Observe(object sender, FileSystemEventArgs args)
    {
        if (args.FullPath.StartsWith(_bootstrap + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) TryCapture();
    }

    private void TryCapture()
    {
        lock (_gate)
        {
            if (_disposed || _copied is not null) return;
            try
            {
                var copied = _bindings.Bindings.ToDictionary(binding => binding.Assembly.Name,
                    binding => WorkerBuildBridgeCandidateBindings.ReadIdentity(Path.Combine(_bootstrap, binding.Assembly.Name + ".dll")));
                // A complete real tool PE is required as well as its dependency copies.
                copied.Add("WorkerBuildScopeTool", WorkerBuildBridgeCandidateBindings.ReadIdentity(Path.Combine(_bootstrap, "WorkerBuildScopeTool.dll")));
                if (_bindings.Bindings.All(binding => copied[binding.Assembly.Name] == binding.Assembly)) _copied = copied;
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException or InvalidOperationException)
            {
                // Copy/compile is in progress. A later filesystem event retries; missing final evidence is RED.
            }
            catch (UnauthorizedAccessException exception)
            {
                _watcherFailure = exception;
            }
        }
    }

    internal string Evidence()
    {
        lock (_gate) return JsonSerializer.Serialize(new { child = _child, hostGeneration = _bindings.Generation, graph = _bindings.SourceGraph.Inputs, bindings = _bindings.Bindings,
            _bindings.HostSnapshotSha256, _bindings.RecheckedHostSnapshotSha256, copied = _copied, watcherFailure = _watcherFailure?.Message });
    }

    internal void Verify()
    {
        lock (_gate)
        {
            WorkerBuildBridgeCandidateBindings.Require(_watcherFailure is null, "Bootstrap observation failed: " + _watcherFailure?.Message);
            WorkerBuildBridgeCandidateBindings.Require(_copied is not null, "Completed real bootstrap and copied dependency identities were not observed.");
        }
        _bindings.VerifyHostUnchanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _scopedWatcher?.Dispose();
            _watcher.Dispose();
        }
    }
}
