using System.ComponentModel;
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
    private Exception? _afterCaptureFailure;
    private bool _disposed;

    internal WorkerBuildBridgeBootstrapCapture(string fixtureRoot, Process child, WorkerBuildBridgeCandidateBindings bindings)
    {
        _bindings = bindings;
        var temp = Path.GetTempPath();
        if (temp.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
            temp = Path.Combine(local, "Temp");
        _processRoot = Path.Combine(temp, "pt", "goal-manual", child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _bootstrap = Path.Combine(_processRoot, "scope-tool", "bin");
        var diagnostics = ReadProcessDiagnostics(() => child.StartTime.ToUniversalTime(), () => child.MainModule?.FileName);
        _child = new { pid = child.Id, startTimeUtc = diagnostics.StartTimeUtc, startTimeError = diagnostics.StartTimeError,
            image = diagnostics.Image, imageError = diagnostics.ImageError, arguments = child.StartInfo.ArgumentList.ToArray(), bootstrap = _bootstrap };
        _watcher = new FileSystemWatcher(temp)
        {
            IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += (_, args) => { if (args.FullPath.Equals(_processRoot, StringComparison.OrdinalIgnoreCase)) ArmScopedWatcher(); };
        _watcher.Error += (_, args) => ObserveFailure(args.GetException());
        _watcher.EnableRaisingEvents = true;
        ArmScopedWatcher();
    }

    // These optional observations are diagnostics only; they do not authorize or prove a bootstrap.
    internal static (DateTime? StartTimeUtc, string? StartTimeError, string? Image, string? ImageError) ReadProcessDiagnostics(
        Func<DateTime> readStartTimeUtc, Func<string?> readImage)
    {
        DateTime? startTimeUtc = null;
        string? startTimeError = null;
        string? image = null;
        string? imageError = null;
        try { startTimeUtc = readStartTimeUtc(); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            startTimeError = exception.ToString();
        }
        try
        {
            image = readImage();
            if (string.IsNullOrWhiteSpace(image))
            {
                image = null;
                imageError = "Process module image metadata is unavailable: MainModule or FileName was null or empty.";
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            imageError = exception.ToString();
        }
        return (startTimeUtc, startTimeError, image, imageError);
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
                _scopedWatcher.Error += (_, args) => ObserveFailure(args.GetException());
                _scopedWatcher.EnableRaisingEvents = true;
                _watcher.EnableRaisingEvents = false;
                TryCapture();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ObserveFailure(exception);
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
                if (_bindings.Bindings.All(binding => copied[binding.Assembly.Name] == binding.Assembly))
                {
                    _copied = copied;
                    // The snapshot is complete. End observation before the real helper removes
                    // its temporary directory; cleanup events cannot invalidate captured bytes.
                    _watcher.EnableRaisingEvents = false;
                    if (_scopedWatcher is not null) _scopedWatcher.EnableRaisingEvents = false;
                }
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException or InvalidOperationException)
            {
                // Copy/compile is in progress. A later filesystem event retries; missing final evidence is RED.
            }
            catch (UnauthorizedAccessException exception)
            {
                ObserveFailure(exception);
            }
        }
    }

    private void ObserveFailure(Exception exception)
    {
        lock (_gate)
        {
            // Preserve pre-snapshot faults even if later events capture all files. A queued
            // watcher callback after completion is retained separately from capture failures.
            if (_copied is null) _watcherFailure ??= exception;
            else _afterCaptureFailure ??= exception;
        }
    }

    internal string Evidence()
    {
        lock (_gate) return JsonSerializer.Serialize(new { child = _child, hostGeneration = _bindings.Generation, graph = _bindings.SourceGraph.Inputs, bindings = _bindings.Bindings,
            _bindings.HostSnapshotSha256, _bindings.RecheckedHostSnapshotSha256, copied = _copied,
            watcherFailure = _watcherFailure?.ToString(), afterCaptureFailure = _afterCaptureFailure?.ToString() });
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
