using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns one held child and its diagnostic reads, from Start through exit/rejection. These
// attempt-specific sidecars never supply build success or completion-build receipt identity.
internal sealed class CompletionBuildHostCapture
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);
    private readonly CompletionBuildEvidenceOperation _operation;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _reads = new();
    private readonly string _attempt = Guid.NewGuid().ToString("N");
    private readonly List<string> _errors = new();
    private Process? _process;
    private StreamCapture? _stdout;
    private StreamCapture? _stderr;
    private Task<string?>? _completion;
    private bool _disposed;
    private bool _authorized;
    private int? _exitCode;
    private int? _heldPid;
    private string _exit = "no-child";
    private Stopwatch? _cleanupElapsed;

    internal CompletionBuildHostCapture(CompletionBuildEvidenceOperation operation, Func<DateTimeOffset> utcNow)
    {
        _operation = operation;
        _utcNow = utcNow;
        MetadataPath = operation.ResultPath + ".host." + _attempt + ".json";
    }

    internal string MetadataPath { get; }
    internal SpawnProcessIdentityReadResult? Registration { get; set; }
    internal string? LaunchFailure { get; set; }
    internal bool HasUnreapedChild => _exit == "termination-unconfirmed";

    internal void Attach(Process process, Func<Process, (TextReader Output, TextReader Error)> readers)
    {
        _process = process; // Establish cleanup ownership before any stream accessor can throw.
        _heldPid = process.Id;
        _exit = "unobserved";
        var pair = readers(process);
        _stdout = new StreamCapture(pair.Output, _reads.Token);
        _stderr = new StreamCapture(pair.Error, _reads.Token);
    }

    internal bool Matches(CompletionBuildEvidenceOperation operation) => Registration?.Identity is { } identity &&
        operation.Id == _operation.Id && operation.ChildPid == identity.ProcessId &&
        operation.ChildStartedAt == identity.StartedAt && operation.ChildImagePath == identity.ImagePath;

    internal void Authorize(string permit)
    {
        lock (_gate)
        {
            if (_disposed || _process is null || _process.HasExited || _stop.IsCancellationRequested || _operation.Deadline <= _utcNow())
                throw new InvalidOperationException("Completion build child is no longer live for authorization.");
            _process.StandardInput.WriteLine(permit);
            _process.StandardInput.Close();
            _authorized = true;
            _completion = ObserveExit(); // Owned and inspected by Collect/Stop; never a discarded drain.
        }
    }

    internal string? Stop()
    {
        lock (_gate)
        {
            if (!_disposed) _stop.Cancel();
            _completion ??= ObserveExit();
        }
        return _completion.GetAwaiter().GetResult();
    }

    internal string? Collect()
    {
        lock (_gate) _completion ??= ObserveExit();
        return _completion.GetAwaiter().GetResult();
    }

    internal static string? ReadRecordedFailure(CompletionBuildEvidenceOperation operation)
    {
        var directory = Path.GetDirectoryName(operation.ResultPath)!;
        if (!Directory.Exists(directory)) return null;
        string[] paths;
        try { paths = Directory.GetFiles(directory, Path.GetFileName(operation.ResultPath) + ".host.*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"Completion build host artifacts unavailable ({directory}): {ex.Message}"; }
        foreach (var path in paths)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var evidence = document.RootElement;
                // A sidecar can only report a fault for its own admitted child. It cannot
                // supply build success or poison a later lease-wait attempt's result.
                if (evidence.GetProperty("OperationId").GetString() != operation.Id ||
                    evidence.GetProperty("TreeDigest").GetString() != operation.TreeDigest ||
                    evidence.GetProperty("DispatchId").GetString() != operation.DispatchId ||
                    !evidence.GetProperty("Authorized").GetBoolean()) continue;
                var identity = evidence.GetProperty("Registration").GetProperty("Identity");
                if (identity.GetProperty("ProcessId").GetInt32() != operation.ChildPid ||
                    identity.GetProperty("StartedAt").GetDateTimeOffset() != operation.ChildStartedAt ||
                    identity.GetProperty("ImagePath").GetString() != operation.ChildImagePath) continue;
                var errors = evidence.GetProperty("Errors").EnumerateArray().Select(error => error.GetString()).ToArray();
                if (errors.Length != 0)
                    return $"Completion build host diagnostics failed ({path}): " + string.Join("; ", errors);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Unattributed diagnostic artifacts have no authority over this child.
                // The matching terminal build receipt is still required independently.
            }
        }
        return null;
    }

    private async Task<string?> ObserveExit()
    {
        try
        {
            if (_process is not null)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var remaining = _operation.Deadline - _utcNow();
                deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                try { await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!_stop.IsCancellationRequested) _errors.Add("Child exceeded the completion-build deadline.");
                    KillHeldChild();
                }
                catch (Exception ex)
                {
                    _errors.Add("Exit observation failed: " + ex.Message);
                    KillHeldChild();
                }

                _cleanupElapsed = Stopwatch.StartNew();
                using var cleanup = new CancellationTokenSource(CleanupBudget);
                // Reserve the final second of the same cleanup budget for cancelling and
                // observing incomplete reads; cancellation does not start another timeout.
                using var drain = new CancellationTokenSource(CleanupBudget - TimeSpan.FromSeconds(1));
                try
                {
                    await _process.WaitForExitAsync(drain.Token).ConfigureAwait(false);
                    _exit = "exited";
                    _exitCode = _process.ExitCode;
                    await Task.WhenAll(Reads()).WaitAsync(drain.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _errors.Add("Exit/drain cleanup incomplete: " + ex.Message);
                    _reads.Cancel();
                    // Cancellation plus closing the actual readers releases pending pipe reads.
                    CloseReaders();
                    try { await Task.WhenAll(Reads()).WaitAsync(cleanup.Token).ConfigureAwait(false); }
                    catch (Exception drainFailure) { _errors.Add("Cancelled drain did not finish: " + drainFailure.Message); }
                }
            }
        }
        catch (Exception ex) { _errors.Add("Host cleanup failed: " + ex.Message); }
        finally
        {
            lock (_gate)
            {
                // No failed admission can leave stdin open or relinquish a live held child.
                if (_process is not null)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            KillHeldChild();
                            var remaining = CleanupBudget - (_cleanupElapsed?.Elapsed ?? TimeSpan.Zero);
                            if (!_process.WaitForExit((int)Math.Max(0, remaining.TotalMilliseconds)))
                                _errors.Add("Held child did not acknowledge termination.");
                        }
                        if (_process.HasExited) { _exit = "exited"; _exitCode = _process.ExitCode; }
                        else _exit = "termination-unconfirmed";
                    }
                    catch (Exception ex)
                    {
                        _exit = "termination-unconfirmed";
                        _errors.Add("Held child termination unconfirmed: " + ex.Message);
                    }
                    try { _process.StandardInput.Close(); }
                    catch (Exception ex) { _errors.Add("Stdin close failed: " + ex.Message); }
                }
                _reads.Cancel();
                CloseReaders();
                // On exceptional OS termination failure, retain the held handle and its
                // coordinator owner. Disposing it would turn an explicit fault into an orphan.
                try { if (!HasUnreapedChild) _process?.Dispose(); }
                catch (Exception ex) { _errors.Add("Process disposal failed: " + ex.Message); }
                _disposed = true;
            }
        }

        WriteDiagnostics();
        _stop.Dispose();
        _reads.Dispose();
        return _errors.Count == 0 ? null : $"Completion build host diagnostics failed ({MetadataPath}): " + string.Join("; ", _errors);
    }

    private void KillHeldChild()
    {
        lock (_gate)
        {
            if (_disposed || _process is null) return;
            try { _process.StandardInput.Close(); }
            catch (Exception ex) { _errors.Add("Stdin close failed: " + ex.Message); }
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex)
            {
                try { if (_process.HasExited) return; }
                catch { /* Preserve the original termination failure when exit is unknown. */ }
                _errors.Add("Held child termination failed: " + ex.Message);
            }
        }
    }

    private IEnumerable<Task> Reads()
    {
        if (_stdout is not null) yield return _stdout.Completion;
        if (_stderr is not null) yield return _stderr.Completion;
    }

    private void CloseReaders()
    {
        foreach (var stream in new[] { _stdout, _stderr })
            try { stream?.Reader.Dispose(); }
            catch (Exception ex) { _errors.Add("Reader close failed: " + ex.Message); }
    }

    private void WriteDiagnostics()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!); }
        catch (Exception ex) { _errors.Add("Artifact directory unavailable: " + ex.Message); }
        SaveStream(_stdout, "stdout");
        SaveStream(_stderr, "stderr");
        try
        {
            var temporary = MetadataPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                OperationId = _operation.Id, AttemptId = _attempt, _operation.TreeDigest, _operation.DispatchId,
                HeldPid = _heldPid, Registration, LaunchFailure, Authorized = _authorized, Exit = _exit, ExitCode = _exitCode,
                StandardOutput = _stdout?.Evidence ?? "unavailable: child/reader not started",
                StandardError = _stderr?.Evidence ?? "unavailable: child/reader not started",
                Errors = _errors.ToArray()
            }));
            File.Move(temporary, MetadataPath);
        }
        catch (Exception ex) { _errors.Add("Host metadata write failed: " + ex.Message); }
    }

    private void SaveStream(StreamCapture? stream, string name)
    {
        if (stream is null) return;
        if (stream.Status != "eof") _errors.Add(name + " capture incomplete: " + stream.Evidence);
        try { File.WriteAllText(Path.ChangeExtension(MetadataPath, name), stream.Content); }
        catch (Exception ex) { _errors.Add(name + " artifact write failed: " + ex.Message); }
    }

    private sealed class StreamCapture
    {
        private const int RetainedCharacterLimit = 20000;
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();
        private long _characters;
        private string? _failure;
        internal TextReader Reader { get; }
        internal Task Completion { get; }
        internal string Status { get; private set; } = "reading";
        internal string Content { get { lock (_gate) return _text.ToString(); } }
        internal string Evidence => $"status={Status} characters={_characters} retained={Content.Length} truncated={_characters > RetainedCharacterLimit} error={_failure}";

        internal StreamCapture(TextReader reader, CancellationToken cancellation)
        {
            Reader = reader;
            Completion = Read(cancellation);
        }

        private async Task Read(CancellationToken cancellation)
        {
            try
            {
                var buffer = new char[4096];
                while (true)
                {
                    var count = await Reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
                    if (count == 0) { Status = "eof"; return; }
                    lock (_gate)
                    {
                        _characters += count;
                        _text.Append(buffer, 0, Math.Min(count, RetainedCharacterLimit - _text.Length));
                    }
                }
            }
            catch (OperationCanceledException) { Status = "cancelled"; }
            catch (Exception ex) { Status = "read-failed"; _failure = ex.ToString(); }
        }
    }
}
