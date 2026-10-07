using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneOutcome(RemoteLaneRequest Request, IRemoteLaneHandle? Handle,
    AcceptanceCheckResult? Accepted);

// One attempt owns executor occupancy and handles. Faults retire an executor for this attempt;
// retained handles exist only to diagnose a result arriving during the local fallback.
internal sealed class RemoteLaneCoordinator : IDisposable
{
    private readonly RemoteLaneExecutorConfiguration _configuration;
    private readonly AcceptancePartitionVerdictCache _cache;
    private readonly IRemoteLaneExecutor _executor;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;
    private readonly string _ledgerPath;
    private readonly string _occupancyRoot;
    private readonly string? _attemptPrefix;
    private readonly Action<string, RemoteLaneOutcomeCode>? _onOutcome;
    private readonly Action<string>? _onEvent;
    private readonly object _gate = new();
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly HashSet<IRemoteLaneHandle> _handles = new(ReferenceEqualityComparer.Instance);

    internal RemoteLaneCoordinator(RemoteLaneExecutorConfiguration configuration,
        AcceptancePartitionVerdictCache cache, string worktreePath, string? attemptPrefix,
        IRemoteLaneExecutor executor, TimeProvider clock, TimeSpan pollInterval,
        Action<string, RemoteLaneOutcomeCode>? onOutcome, Action<string>? onEvent = null)
    {
        _configuration = configuration;
        _cache = cache;
        _executor = executor;
        _clock = clock;
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromSeconds(1);
        _ledgerPath = RemoteExecutorHealthLedger.ResolveStorePath(worktreePath);
        _occupancyRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath);
        _attemptPrefix = attemptPrefix;
        _onOutcome = onOutcome;
        _onEvent = onEvent;
    }

    internal bool IsEligible(Check check)
    {
        if (!_configuration.Lanes.Contains(check.Name, StringComparer.Ordinal) ||
            !GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _)) return false;
        if (check.ExclusiveResourceKeys.Count == 0) return true;
        bool first;
        lock (_gate) first = _excluded.Add(check.Name);
        if (first) Record(CreateRequest(check, ""), RemoteLaneOutcomeCode.NotEligibleExclusiveResource);
        return false;
    }

    internal RemoteLaneExecutorEntry? TryClaimIdleExecutor()
    {
        lock (_gate)
        {
            var entry = _configuration.Executors.FirstOrDefault(entry => !_busy.Contains(entry.Id) && !_retired.Contains(entry.Id));
            if (entry is not null) _busy.Add(entry.Id);
            if (entry is not null) RemoteExecutorOccupancy.Claim(_occupancyRoot, entry.Id, _cache.AttemptId);
            return entry;
        }
    }

    internal void Release(string executorId)
    {
        lock (_gate) _busy.Remove(executorId);
        RemoteExecutorOccupancy.Release(_occupancyRoot, executorId, _cache.AttemptId);
    }

    private RemoteLaneRequest CreateRequest(Check check, string executorId)
    {
        GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out var filter);
        return new(executorId, _cache.AttemptId, _cache.GoalId, check.Name, check.Project!, filter,
            GoalAcceptanceVerifier.ShortHash(filter), _cache.VerifyingCommitSha, _cache.CandidateTreeSha,
            _cache.MainSha, _cache.ManifestIdentity);
    }

    internal async Task<RemoteLaneOutcome> RunRemoteAsync(Check check, RemoteLaneExecutorEntry entry,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var request = CreateRequest(check, entry.Id);
        var submittedAt = _clock.GetUtcNow();
        IRemoteLaneHandle? handle = null;
        RemoteLaneResult? observedResult = null;
        var inspectingTrx = false;
        IReadOnlyList<RemoteLaneStep> steps = [];
        string? exceptionMessage = null;
        try
        {
            var submission = await _executor.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            handle = submission.Handle;
            steps = submission.Steps ?? [];
            if (handle is null)
                return await Fallback(submission.FailureReason == "transport-unavailable"
                    ? RemoteLaneOutcomeCode.TransportUnavailable : RemoteLaneOutcomeCode.Unreachable,
                    reason: submission.FailureReason).ConfigureAwait(false);
            lock (_gate) _handles.Add(handle);
            var heartbeat = submittedAt;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handle.NewestHeartbeat is { } newest && newest > heartbeat) heartbeat = newest;
                var now = _clock.GetUtcNow();
                if (now - submittedAt > timeout) return await Fallback(RemoteLaneOutcomeCode.LaneTimeout).ConfigureAwait(false);
                if (now - heartbeat > TimeSpan.FromSeconds(entry.LeaseSeconds)) return await Fallback(RemoteLaneOutcomeCode.LeaseExpired).ConfigureAwait(false);
                if (handle.TryGetResult() is { } result)
                {
                    observedResult = result;
                    if (BindingMismatch(request, result) is { } mismatch) return await Fallback(mismatch, result).ConfigureAwait(false);
                    inspectingTrx = true;
                    var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(result.TestResultPaths);
                    var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                        new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""), trx, requireTrxEvidence: true);
                    if (!decision.Passed)
                    {
                        var red = decision.FailedPredicate is AcceptanceShardCompletionPredicates.FailingTrx or
                            AcceptanceShardCompletionPredicates.AssemblyCleanupFailure or AcceptanceShardCompletionPredicates.NonzeroExit;
                        return await Fallback(red ? RemoteLaneOutcomeCode.RemoteRed : RemoteLaneOutcomeCode.TrxIncomplete, result).ConfigureAwait(false);
                    }
                    var paths = GoalAcceptanceVerifierTestTelemetry.CopyCompletedTestReceiptsToAttemptFolder(result.TestResultPaths, _attemptPrefix);
                    // Custody is part of acceptance: a missing or unretained receipt falls back too.
                    var folder = Path.GetDirectoryName(_attemptPrefix);
                    if (paths is null || paths.Count == 0 || string.IsNullOrWhiteSpace(folder) ||
                        paths.Any(path => !File.Exists(path) ||
                            !Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        return await Fallback(RemoteLaneOutcomeCode.TrxIncomplete, result).ConfigureAwait(false);
                    var retained = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(paths);
                    if (!GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                        new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""), retained, requireTrxEvidence: true).Passed)
                        return await Fallback(RemoteLaneOutcomeCode.TrxIncomplete, result).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var accepted = new AcceptanceCheckResult(check.Name, true, result.ExitCode, null,
                        ResultSummary: $"remote-executor={entry.Id}", TestResultPaths: paths,
                        TestResultAttemptId: _cache.AttemptId, ExecutedTestCount: trx.ExecutedTestCount,
                        DiscoveredTestCount: trx.DiscoveredTestCount, CompletionDecision: decision, TestProjectPath: check.Project);
                    inspectingTrx = false;
                    Record(request, RemoteLaneOutcomeCode.Accepted, result, attempt: Detail());
                    Release(entry.Id);
                    Abandon(handle);
                    return new(request, null, accepted);
                }
                // Cadence uses real time; decisions use only the injected clock.
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            exceptionMessage = ex.Message;
            return await Fallback(inspectingTrx ? RemoteLaneOutcomeCode.TrxIncomplete : RemoteLaneOutcomeCode.Unreachable,
                observedResult, reason: $"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        }

        async Task<RemoteLaneOutcome> Fallback(RemoteLaneOutcomeCode code, RemoteLaneResult? result = null, string? reason = null)
        {
            lock (_gate) { _busy.Remove(entry.Id); _retired.Add(entry.Id); }
            RemoteLaneRunnerLogCapture? capture = null;
            if (handle is IRemoteLaneAttemptDiagnosticsSource source)
            {
                try
                {
                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    bound.CancelAfter(TimeSpan.FromSeconds(30));
                    // The source bounds its transport and file reads and returns after its cleanup.
                    capture = await source.CaptureRunnerLogAsync(bound.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* Diagnostics cannot change fallback. */ }
            }
            RemoteExecutorOccupancy.Release(_occupancyRoot, entry.Id, _cache.AttemptId);
            Record(request, code, result, reason, Detail(capture));
            return new(request, handle, null);
        }

        RemoteLaneAttemptDetail Detail(RemoteLaneRunnerLogCapture? capture = null)
        {
            RemoteLaneHandleDiagnostics? snapshot = null;
            try { snapshot = (handle as IRemoteLaneAttemptDiagnosticsSource)?.Snapshot(); }
            catch (Exception) { /* Optional diagnostics cannot change the decision. */ }
            return new(capture?.FailedStep is { } failed ? steps.Concat([failed]).ToArray() : steps,
                snapshot?.Poll, snapshot?.Fetches ?? [], snapshot?.LastStatus, capture?.Path, exceptionMessage);
        }
    }

    internal static RemoteLaneOutcomeCode? BindingMismatch(RemoteLaneRequest request, RemoteLaneResult result)
    {
        if (result.VerifyingCommitSha != request.VerifyingCommitSha) return RemoteLaneOutcomeCode.BindingMismatchCommit;
        if (result.ObservedTreeSha != request.CandidateTreeSha) return RemoteLaneOutcomeCode.BindingMismatchTree;
        if (result.MainSha != request.MainSha) return RemoteLaneOutcomeCode.BindingMismatchMain;
        if (result.FilterHash != request.FilterHash || result.Lane != request.Lane) return RemoteLaneOutcomeCode.BindingMismatchFilter;
        if (result.ExecutorId != request.ExecutorId) return RemoteLaneOutcomeCode.BindingMismatchExecutor;
        if (result.ManifestIdentity != request.ManifestIdentity) return RemoteLaneOutcomeCode.BindingMismatchManifest;
        return null;
    }

    private void Record(RemoteLaneRequest request, RemoteLaneOutcomeCode code, RemoteLaneResult? result = null,
        string? reason = null, RemoteLaneAttemptDetail? attempt = null)
    {
        RemoteExecutorHealthLedger.Append(_ledgerPath, new(_clock.GetUtcNow(), request.ExecutorId,
            request.AttemptId, request.Lane, code,
            code is RemoteLaneOutcomeCode.Accepted or RemoteLaneOutcomeCode.NotEligibleExclusiveResource ? null : "executor",
            new(request.ExecutorId, request.Lane, request.VerifyingCommitSha, request.CandidateTreeSha,
                request.MainSha, request.FilterHash, request.ManifestIdentity),
            result is null ? null : new(result.ExecutorId, result.Lane, result.VerifyingCommitSha, result.ObservedTreeSha,
                result.MainSha, result.FilterHash, result.ManifestIdentity), reason, Attempt: attempt));
        try
        {
            var compactReason = string.IsNullOrWhiteSpace(reason) ? "none" :
                System.Text.RegularExpressions.Regex.Replace(reason, @"\s+", " ").Trim();
            if (compactReason.Length > 200) compactReason = compactReason[..200];
            var lane = request.Lane.Any(char.IsWhiteSpace) ? $"\"{request.Lane.Replace('"', '\'')}\"" : request.Lane;
            _onEvent?.Invoke($"REMOTE_LANE executor={(string.IsNullOrEmpty(request.ExecutorId) ? "none" : request.ExecutorId)} " +
                $"lane={lane} attempt={request.AttemptId} outcome={System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(code.ToString())} reason={compactReason}");
        }
        catch (Exception) { /* Event reporting is observational. */ }
        _onOutcome?.Invoke(request.Lane, code);
    }

    internal void PollLateOnce(RemoteLaneOutcome outcome)
    {
        if (outcome.Handle is not { } handle) return;
        try
        {
            if (handle.TryGetResult() is { } late) Record(outcome.Request, RemoteLaneOutcomeCode.LateAfterFallback, late);
        }
        catch (Exception) { /* Diagnostics cannot change the local verdict. */ }
        finally { Abandon(handle); }
    }

    private void Abandon(IRemoteLaneHandle handle)
    {
        lock (_gate) { if (!_handles.Remove(handle)) return; }
        try { handle.Abandon(); }
        catch (Exception) { /* Best effort; no remote fault can fail a goal. */ }
    }

    internal void AbandonAll()
    {
        IRemoteLaneHandle[] handles;
        lock (_gate) handles = _handles.ToArray();
        foreach (var handle in handles) Abandon(handle);
    }

    public void Dispose() => AbandonAll();
}
