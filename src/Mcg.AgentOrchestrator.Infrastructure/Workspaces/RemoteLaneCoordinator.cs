using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneOutcome(RemoteLaneRequest Request, IRemoteLaneHandle? Handle,
    AcceptanceCheckResult? Accepted, TimeSpan? RemoteDuration = null);

// One attempt owns executor occupancy and handles. Faults retire an executor for this attempt;
// retained handles exist only to diagnose a result arriving during the local fallback.
internal sealed class RemoteLaneCoordinator : IDisposable
{
    private readonly RemoteLaneExecutorConfiguration _configuration;
    private readonly IRemoteLaneCandidateIdentity _cache;
    private readonly IRemoteLaneExecutor _executor;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;
    private readonly string _ledgerPath;
    private readonly string _occupancyRoot;
    private readonly string? _attemptPrefix;
    private readonly Action<string, RemoteLaneOutcomeCode>? _onOutcome;
    private readonly Action<string>? _onEvent;
    private readonly object _gate = new();
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly HashSet<IRemoteLaneHandle> _handles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SortedDictionary<int, IDisposable>> _claims = new(StringComparer.Ordinal);

    internal RemoteLaneCoordinator(RemoteLaneExecutorConfiguration configuration,
        IRemoteLaneCandidateIdentity cache, string worktreePath, string? attemptPrefix,
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
        if (!check.RequiresBuildSystemChange && check.ExclusiveResourceKeys.All(key =>
                _configuration.MachineLocalResourceKeys.Contains(key, StringComparer.Ordinal))) return true;
        bool first;
        lock (_gate) first = _excluded.Add(check.Name);
        if (first) Record(CreateRequest(check, ""), RemoteLaneOutcomeCode.NotEligibleExclusiveResource);
        return false;
    }

    internal RemoteLaneExecutorEntry? TryClaimIdleExecutor()
    {
        lock (_gate)
        {
            // OrderBy is stable: equally occupied executors retain configuration order.
            foreach (var entry in _configuration.Executors
                .Where(entry => !_retired.Contains(entry.Id) && HeldSlotCount(entry.Id) < entry.Slots)
                .OrderBy(entry => HeldSlotCount(entry.Id)))
            {
                _claims.TryGetValue(entry.Id, out var slots);
                for (var slotIndex = 0; slotIndex < entry.Slots; slotIndex++)
                {
                    if (slots?.ContainsKey(slotIndex) == true) continue;
                    var claim = RemoteExecutorOccupancy.TryClaimExclusive(_occupancyRoot, entry.Id, slotIndex);
                    if (claim is null) continue;
                    if (slots is null) _claims.Add(entry.Id, slots = new());
                    slots.Add(slotIndex, claim);
                    RemoteExecutorOccupancy.Claim(_occupancyRoot, entry.Id, _cache.AttemptId, slotIndex);
                    return entry;
                }
            }
            return null;
        }
    }

    internal void Release(string executorId)
    {
        lock (_gate)
        {
            if (TakeHighestSlot(executorId) is { } slot)
            {
                RemoteExecutorOccupancy.Release(_occupancyRoot, executorId, _cache.AttemptId, slot.Key);
                slot.Value.Dispose();
            }
        }
    }

    private int HeldSlotCount(string executorId) => _claims.TryGetValue(executorId, out var slots) ? slots.Count : 0;

    // Caller holds _gate. A lane returns one interchangeable slot, leaving its peers held.
    private KeyValuePair<int, IDisposable>? TakeHighestSlot(string executorId)
    {
        if (!_claims.TryGetValue(executorId, out var slots)) return null;
        var slot = slots.Last();
        slots.Remove(slot.Key);
        if (slots.Count == 0) _claims.Remove(executorId);
        return slot;
    }

    private RemoteLaneRequest CreateRequest(Check check, string executorId)
    {
        GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out var filter);
        return new(executorId, _cache.AttemptId, _cache.GoalId, check.Name, check.Project!, filter,
            GoalAcceptanceVerifier.ShortHash(filter), _cache.VerifyingCommitSha, _cache.CandidateTreeSha,
            _cache.MainSha, _cache.ManifestIdentity);
    }

    internal RemoteLaneRequest CreateRequest(string lane, string project, string filter,
        IRemoteLaneCandidateIdentity identity, string executorId) =>
        new(executorId, identity.AttemptId, identity.GoalId, lane, project, filter,
            GoalAcceptanceVerifier.ShortHash(filter), identity.VerifyingCommitSha, identity.CandidateTreeSha,
            identity.MainSha, identity.ManifestIdentity);

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
                    if (check.ExclusiveResourceKeys.Count > 0 && trx.NotExecutedTestCount is not 0)
                        return await Fallback(RemoteLaneOutcomeCode.UnexpectedNotExecuted, result,
                            reason: $"not_executed={trx.NotExecutedTestCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}").ConfigureAwait(false);
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
                    var detail = Detail();
                    var remoteDuration = ResolveRemoteDuration(detail.LastStatus, _clock.GetUtcNow() - submittedAt);
                    Record(request, RemoteLaneOutcomeCode.Accepted, result, attempt: detail);
                    Release(entry.Id);
                    Abandon(handle);
                    return new(request, null, accepted, remoteDuration);
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
            KeyValuePair<int, IDisposable>? slot;
            lock (_gate)
            {
                _retired.Add(entry.Id);
                slot = TakeHighestSlot(entry.Id);
            }
            if (observedResult is null && result is null && handle is IRemoteLaneQueuedJobCancellation cancellation)
                try { cancellation.RequestQueuedJobCancellation(); }
                catch (Exception) { /* Best effort; cannot change fallback. */ }
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
            if (slot is { } held)
            {
                RemoteExecutorOccupancy.Release(_occupancyRoot, entry.Id, _cache.AttemptId, held.Key);
                held.Value.Dispose();
            }
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

    private static TimeSpan ResolveRemoteDuration(JsonElement? lastStatus, TimeSpan elapsed)
    {
        try
        {
            if (lastStatus is { ValueKind: JsonValueKind.Object } status &&
                status.TryGetProperty("seconds", out var seconds) &&
                seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out var value) &&
                double.IsFinite(value) && value > 0)
                return TimeSpan.FromSeconds(value);
        }
        catch (OverflowException) { /* An unrepresentable duration uses the clock observation. */ }
        catch (InvalidOperationException) { /* Unavailable diagnostics cannot change acceptance. */ }
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
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
        lock (_gate) { handles = _handles.ToArray(); _handles.Clear(); }
        foreach (var handle in handles)
        {
            try { (handle as IRemoteLaneQueuedJobCancellation)?.RequestQueuedJobCancellation(); }
            catch (Exception) { /* Best effort; cannot change cancellation. */ }
            try { handle.Abandon(); }
            catch (Exception) { /* Best effort; no remote fault can fail a goal. */ }
        }
        ReleaseAllClaims();
    }

    public void Dispose()
    {
        AbandonAll();
        ReleaseAllClaims();
    }

    private void ReleaseAllClaims()
    {
        lock (_gate)
        {
            foreach (var (executorId, slots) in _claims)
            {
                foreach (var (slotIndex, claim) in slots)
                {
                    RemoteExecutorOccupancy.Release(_occupancyRoot, executorId, _cache.AttemptId, slotIndex);
                    claim.Dispose();
                }
            }
            _claims.Clear();
        }
    }
}
