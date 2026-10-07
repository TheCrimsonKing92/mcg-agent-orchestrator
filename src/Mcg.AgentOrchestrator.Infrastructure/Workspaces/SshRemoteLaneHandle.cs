namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class SshRemoteLaneHandle : IRemoteLaneHandle, IRemoteLaneAttemptDiagnosticsSource, IRemoteLaneQueuedJobCancellation
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _heartbeat;
    private RemoteLaneResult? _result;
    private Exception? _fault;
    private readonly string _staging, _remote;
    private readonly string _cancelName, _queueTarget;
    private bool _jobStarted;
    internal Task? QueuedJobCancellation { get; private set; }
    private readonly TimeProvider _clock;
    private readonly Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> _transport;
    private int _pollCount, _failedPollCount;
    private string? _lastState;
    private System.Text.Json.JsonElement? _lastStatus;
    private RemoteLaneStep? _lastFailedPoll;
    private readonly List<RemoteLaneStep> _fetches = new();
    internal Task PollLoop { get; }
    public DateTimeOffset? NewestHeartbeat { get { lock (_sync) return _heartbeat; } }
    public RemoteLaneResult? TryGetResult()
    {
        lock (_sync)
        {
            if (_fault is not null) throw _fault;
            return _result;
        }
    }
    public void Abandon() => _stop.Cancel();
    public void RequestQueuedJobCancellation()
    {
        lock (_sync)
        {
            if (_jobStarted || QueuedJobCancellation is not null) return;
            QueuedJobCancellation = Task.Run(async () =>
            {
                try
                {
                    using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await File.WriteAllTextAsync(Path.Combine(_staging, "cancel.txt"), "", bound.Token).ConfigureAwait(false);
                    await _transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", "cancel.txt", _queueTarget + _cancelName],
                        _staging, TimeSpan.FromSeconds(30), bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* Best effort; queued cancellation cannot change the gate outcome. */ }
            });
        }
    }
    public RemoteLaneHandleDiagnostics Snapshot()
    {
        lock (_sync) return new(new(_pollCount, _failedPollCount, _lastState, _heartbeat, _lastFailedPoll),
            _fetches.ToArray(), _lastStatus);
    }

    internal SshRemoteLaneHandle(RemoteLaneRequest request, RemoteLaneExecutorEntry entry, string staging,
        string shortSha, string laneKey, TimeProvider clock,
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
        TimeSpan pollInterval, Action<SshPollObservation>? onPollCompleted)
    {
        _staging = staging;
        _cancelName = $"{request.AttemptId}-{laneKey}.cancel";
        _queueTarget = $"{entry.RunnerAlias}:{entry.RunRoot}/queue/";
        _remote = $"{entry.RunnerAlias}:{entry.RunRoot}/results/{shortSha}/{laneKey}/";
        _clock = clock;
        _transport = transport;
        PollLoop = Task.Run(async () =>
        {
            var remote = _remote;
            string? previousHeartbeat = null;
            try
            {
                for (var index = 1; ; index++)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    var folder = Path.Combine(staging, $"poll-{index}-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(folder);
                    try
                    {
                        _stop.Token.ThrowIfCancellationRequested();
                        var startedAt = clock.GetUtcNow();
                        var poll = await transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
                            remote + "status.json", remote + "heartbeat.txt", Path.GetFileName(folder)], staging,
                            TimeSpan.FromMinutes(2), _stop.Token).ConfigureAwait(false);
                        var pollStep = RemoteLaneDiagnosticFiles.Step(staging, "poll", poll.ExitCode, poll.TimedOut,
                            startedAt, clock.GetUtcNow(), poll.Output, poll.Stderr);
                        lock (_sync)
                        {
                            _pollCount++;
                            if (poll.ExitCode != 0 || poll.TimedOut) { _failedPollCount++; _lastFailedPoll = pollStep; }
                        }
                        _stop.Token.ThrowIfCancellationRequested();
                        if (poll.ExitCode != 0 || poll.TimedOut) continue;
                        var status = SshRemoteLaneStatus.Read(Path.Combine(folder, "status.json"));
                        if (status is not null)
                            lock (_sync) { _lastStatus = status.Fields; _lastState = status.String("state"); }
                        if (status is null || status.String("attemptId") != request.AttemptId) continue;
                        lock (_sync) _jobStarted = true;
                        try
                        {
                            var heartbeat = File.ReadAllText(Path.Combine(folder, "heartbeat.txt"));
                            if (heartbeat != previousHeartbeat)
                            {
                                lock (_sync) _heartbeat = clock.GetUtcNow();
                                previousHeartbeat = heartbeat;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        if (status.String("state") == "failed") throw new RemoteLaneTransportException(status.String("error"));
                        if (status.String("state") != "completed") continue;
                        var (code, names) = status.Completion();
                        _stop.Token.ThrowIfCancellationRequested();
                        var paths = names.Select(name => Path.Combine(staging, name)).ToArray();
                        var temporaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            for (var resultIndex = 0; resultIndex < names.Length; resultIndex++)
                            {
                                // scp needs a short local path; .NET can publish the full TRX name.
                                var (localName, temporaryPath) = ReserveFetchName(staging, temporaryPaths);

                                startedAt = clock.GetUtcNow();
                                var fetch = await transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
                                    remote + names[resultIndex], localName], staging,
                                    TimeSpan.FromMinutes(2), _stop.Token).ConfigureAwait(false);
                                var fetchStep = RemoteLaneDiagnosticFiles.Step(staging, $"fetch-{resultIndex + 1}",
                                    fetch.ExitCode, fetch.TimedOut, startedAt, clock.GetUtcNow(), fetch.Output, fetch.Stderr);
                                lock (_sync) _fetches.Add(fetchStep);
                                _stop.Token.ThrowIfCancellationRequested();
                                if (fetch.ExitCode != 0 || fetch.TimedOut || !File.Exists(temporaryPath))
                                    throw FetchFailure(fetch);
                                try { File.Move(temporaryPath, paths[resultIndex], overwrite: true); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                { throw FetchFailure(fetch); }
                            }
                        }
                        finally
                        {
                            foreach (var temporaryPath in temporaryPaths)
                            {
                                try { File.Delete(temporaryPath); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                            }
                        }
                        lock (_sync) _result = status.Result(code, paths);
                        return;
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                    catch (Exception ex) { lock (_sync) _fault = ex; return; }
                    finally
                    {
                        onPollCompleted?.Invoke(new(index, folder));
                        // Pacing has no bearing on the lease; only the injected coordinator clock does.
                        if (!_stop.IsCancellationRequested && _result is null && _fault is null)
                            await Task.Delay(pollInterval, _stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { lock (_sync) _fault = ex; }
        });
    }

    private static (string Name, string Path) ReserveFetchName(string staging, HashSet<string> temporaryPaths)
    {
        string name, path;
        do
        {
            name = $"f-{Guid.NewGuid().ToString("N")[..8]}.part";
            path = Path.Combine(staging, name);
        } while (Path.Exists(path) || !temporaryPaths.Add(path));
        return (name, path);
    }

    public async Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken cancellationToken)
    {
        var temporaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startedAt = _clock.GetUtcNow();
        GoalAcceptanceVerifier.CommandResult? capture = null;
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(30));
            var (name, path) = ReserveFetchName(_staging, temporaryPaths);
            capture = await _transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes", _remote + "runner.log", name],
                _staging, TimeSpan.FromSeconds(30), bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
            if (capture.ExitCode != 0 || capture.TimedOut || !File.Exists(path))
                return FailedCapture(capture);
            // Scan incrementally, retaining at most 200 bounded line tails even for an oversized line.
            var lines = new Queue<System.Text.StringBuilder>();
            var current = new System.Text.StringBuilder();
            var sawCarriageReturn = false;
            using var reader = new StreamReader(path);
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), bound.Token).ConfigureAwait(false)) != 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var ch = buffer[index];
                    if (ch == '\n' && sawCarriageReturn) { sawCarriageReturn = false; continue; }
                    sawCarriageReturn = ch == '\r';
                    if (ch is '\r' or '\n')
                    {
                        if (current.Length > RemoteLaneDiagnosticFiles.MaxBytes)
                            current.Remove(0, current.Length - RemoteLaneDiagnosticFiles.MaxBytes);
                        lines.Enqueue(current); current = new();
                        if (lines.Count > 200) lines.Dequeue();
                    }
                    else current.Append(ch);
                }
                if (current.Length > RemoteLaneDiagnosticFiles.MaxBytes)
                    current.Remove(0, current.Length - RemoteLaneDiagnosticFiles.MaxBytes);
            }
            if (current.Length > 0) { lines.Enqueue(current); if (lines.Count > 200) lines.Dequeue(); }
            bound.Token.ThrowIfCancellationRequested();
            var tailPath = RemoteLaneDiagnosticFiles.WriteTail(_staging, "runner-tail.log",
                string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return tailPath is null ? FailedCapture(capture) : new(tailPath);
        }
        catch (Exception ex)
        {
            return new(null, RemoteLaneDiagnosticFiles.Step(_staging, "runner-log", capture?.ExitCode,
                capture?.TimedOut == true || ex is OperationCanceledException, startedAt, _clock.GetUtcNow(),
                capture?.Output ?? "", capture?.Stderr ?? ex.Message, failed: true));
        }
        finally
        {
            foreach (var path in temporaryPaths)
                try { File.Delete(path); } catch (Exception) { }
        }

        RemoteLaneRunnerLogCapture FailedCapture(GoalAcceptanceVerifier.CommandResult result) => new(null,
            RemoteLaneDiagnosticFiles.Step(_staging, "runner-log", result.ExitCode, result.TimedOut,
                startedAt, _clock.GetUtcNow(), result.Output, result.Stderr, failed: true));
    }

    private static RemoteLaneTransportException FetchFailure(GoalAcceptanceVerifier.CommandResult fetch)
    {
        var error = (string.IsNullOrEmpty(fetch.Stderr) ? fetch.Output : fetch.Stderr).Trim();
        var tail = error.Length > 2048 ? error[^2048..] : error;
        return new($"result-fetch-failed exit={fetch.ExitCode} timedOut={fetch.TimedOut} stderr={tail}");
    }
}
