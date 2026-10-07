namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class SshRemoteLaneHandle : IRemoteLaneHandle
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _heartbeat;
    private RemoteLaneResult? _result;
    private Exception? _fault;
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

    internal SshRemoteLaneHandle(RemoteLaneRequest request, RemoteLaneExecutorEntry entry, string staging,
        string shortSha, string laneKey, TimeProvider clock,
        Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
        TimeSpan pollInterval, Action<SshPollObservation>? onPollCompleted)
    {
        PollLoop = Task.Run(async () =>
        {
            var remote = $"{entry.RunnerAlias}:{entry.RunRoot}/results/{shortSha}/{laneKey}/";
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
                        var poll = await transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
                            remote + "status.json", remote + "heartbeat.txt", Path.GetFileName(folder)], staging,
                            TimeSpan.FromMinutes(2), _stop.Token).ConfigureAwait(false);
                        _stop.Token.ThrowIfCancellationRequested();
                        if (poll.ExitCode != 0 || poll.TimedOut) continue;
                        var status = SshRemoteLaneStatus.Read(Path.Combine(folder, "status.json"));
                        if (status is null || status.String("attemptId") != request.AttemptId) continue;
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
                                string localName, temporaryPath;
                                do
                                {
                                    localName = $"f-{Guid.NewGuid().ToString("N")[..8]}.part";
                                    temporaryPath = Path.Combine(staging, localName);
                                } while (Path.Exists(temporaryPath) || !temporaryPaths.Add(temporaryPath));

                                var fetch = await transport([SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
                                    remote + names[resultIndex], localName], staging,
                                    TimeSpan.FromMinutes(2), _stop.Token).ConfigureAwait(false);
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

    private static RemoteLaneTransportException FetchFailure(GoalAcceptanceVerifier.CommandResult fetch)
    {
        var error = (string.IsNullOrEmpty(fetch.Stderr) ? fetch.Output : fetch.Stderr).Trim();
        var tail = error.Length > 2048 ? error[^2048..] : error;
        return new($"result-fetch-failed exit={fetch.ExitCode} timedOut={fetch.TimedOut} stderr={tail}");
    }
}
