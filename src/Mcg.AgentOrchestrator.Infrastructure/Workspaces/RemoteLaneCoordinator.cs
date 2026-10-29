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
    private readonly string _worktreePath;
    private readonly string _occupancyRoot;
    private readonly string? _attemptPrefix;
    private readonly Action<string, RemoteLaneOutcomeCode>? _onOutcome;
    private readonly Action<string>? _onEvent;
    private readonly RemoteExecutorClaimClass _claimClass;
    private readonly RemoteExecutorSlotArbiter _arbiter;
    private readonly HashSet<string> _noSlotLanes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _busyUntil = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly HashSet<IRemoteLaneHandle> _handles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SortedDictionary<int, IDisposable>> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _keyClaims = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds> RemoteLaneHistory { get; init; }
        = new Dictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds>();
    internal RemoteLaneOccupancyHeartbeat? Occupancy { get; set; }

    internal RemoteLaneCoordinator(RemoteLaneExecutorConfiguration configuration,
        IRemoteLaneCandidateIdentity cache, string worktreePath, string? attemptPrefix,
        IRemoteLaneExecutor executor, TimeProvider clock, TimeSpan pollInterval,
        Action<string, RemoteLaneOutcomeCode>? onOutcome, Action<string>? onEvent = null,
        RemoteExecutorClaimClass claimClass = RemoteExecutorClaimClass.Gate)
    {
        _configuration = configuration;
        _worktreePath = worktreePath;
        _cache = cache;
        _executor = executor;
        _clock = clock;
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromSeconds(1);
        _ledgerPath = RemoteExecutorHealthLedger.ResolveStorePath(worktreePath);
        _occupancyRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath);
        _attemptPrefix = attemptPrefix;
        _onOutcome = onOutcome;
        _onEvent = onEvent;
        _claimClass = claimClass;
        _arbiter = new(_occupancyRoot, cache.AttemptId, claimClass, clock);
    }

    internal bool IsEligible(Check check)
    {
        if (!_configuration.Lanes.Contains(check.Name, StringComparer.Ordinal) ||
            !GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _)) return false;
        var eligibleKeys = _configuration.ExecutorSerializedResourceKeys.Count == 0
            ? _configuration.MachineLocalResourceKeys
            : _configuration.MachineLocalResourceKeys.Union(_configuration.ExecutorSerializedResourceKeys, StringComparer.Ordinal).ToArray();
        if (RemoteLaneResourceEligibility.ExclusionReason(check.ExclusiveResourceKeys,
                check.RequiresBuildSystemChange, eligibleKeys) is null) return true;
        bool first;
        lock (_gate) first = _excluded.Add(check.Name);
        if (first) Record(CreateRequest(check, ""), RemoteLaneOutcomeCode.NotEligibleExclusiveResource);
        return false;
    }

    internal RemoteLaneExecutorEntry? TryClaimIdleExecutor() => TryClaimIdleExecutor(out _);

    internal RemoteLaneExecutorEntry? TryClaimIdleExecutor(out string? yieldReason)
    {
        lock (_gate)
        {
            yieldReason = _configuration.Executors.Any(entry => entry.AdmissionEnabled) ? null : "no-enabled-executor";
            // OrderBy is stable: equally occupied executors retain configuration order.
            foreach (var entry in EligibleExecutors())
            {
                if (_arbiter.YieldReason(entry.Id, _configuration.FocusedEvidence) is { } reason)
                {
                    if (yieldReason is null || reason == "yield-to-gate" ||
                        reason == "yield-to-focused-evidence" && yieldReason == "idle-only") yieldReason = reason;
                    continue;
                }
                if (TryClaimOn(entry.Id) is { } claimed) { yieldReason = null; return claimed; }
            }
            if (_claimClass == RemoteExecutorClaimClass.FocusedEvidence)
                _arbiter.WriteDemand(_configuration.Executors.Where(entry => entry.AdmissionEnabled).Select(entry => entry.Id));
            return null;
        }
    }

    private IEnumerable<RemoteLaneExecutorEntry> EligibleExecutors() => _configuration.Executors
        .Where(entry => entry.AdmissionEnabled && !_retired.Contains(entry.Id) && HeldSlotCount(entry.Id) < entry.Slots &&
            (!_busyUntil.TryGetValue(entry.Id, out var until) || until <= _clock.GetUtcNow()))
        .OrderBy(entry => HeldSlotCount(entry.Id));

    private RemoteLaneExecutorEntry? TryClaimOn(string executorId)
    {
        lock (_gate)
        {
            var entry = EligibleExecutors().FirstOrDefault(entry => entry.Id == executorId);
            if (entry is null) return null;
            _claims.TryGetValue(entry.Id, out var slots);
            for (var slotIndex = 0; slotIndex < entry.Slots; slotIndex++)
            {
                if (slots?.ContainsKey(slotIndex) == true) continue;
                var claim = RemoteExecutorOccupancy.TryClaimExclusive(_occupancyRoot, entry.Id, slotIndex);
                if (claim is null) continue;
                if (slots is null) _claims.Add(entry.Id, slots = new());
                slots.Add(slotIndex, claim);
                RemoteExecutorOccupancy.Claim(_occupancyRoot, entry.Id, _cache.AttemptId, slotIndex, _claimClass);
                _arbiter.Claimed(entry.Id);
                return entry;
            }
            return null;
        }
    }

    internal async Task<RemoteLaneExecutorEntry?> ClaimForGateAsync(Check check, CancellationToken cancellationToken)
    {
        if (_claimClass != RemoteExecutorClaimClass.Gate) throw new InvalidOperationException("Only Gate claims may preempt.");
        var keys = check.ExclusiveResourceKeys.Intersect(_configuration.ExecutorSerializedResourceKeys, StringComparer.Ordinal).ToArray();
        if (keys.Length > 0) return await ClaimWithSerializedKeysAsync(check, keys, cancellationToken).ConfigureAwait(false);
        if (TryClaimIdleExecutor() is { } idle) return idle;
        RemoteExecutorSlotArbiter.Target? target = null;
        var settings = _configuration.FocusedEvidence;
        lock (_gate)
            if (settings.FaultReason is null && settings.Mode == "shadow" && settings.ShadowPreemption == "cancellable")
                target = _arbiter.RequestPreemption(EligibleExecutors().Select(entry => entry.Id), check.Name);
        if (target is not null && await _arbiter.WaitForGateAsync(target, _pollInterval, TryClaimOn,
            (released, waited) => Record(CreateRequest(check, ""), RemoteLaneOutcomeCode.ShadowPreemptRequested,
                reason: $"shadow_attempt={target.ShadowAttempt} executor={target.Executor} released={released.ToString().ToLowerInvariant()} waited_ms={waited}"),
            cancellationToken).ConfigureAwait(false) is { } claimed) return claimed;
        RecordNoSlot(check);
        _arbiter.WriteDemand(_configuration.Executors.Where(entry => entry.AdmissionEnabled).Select(entry => entry.Id));
        return null;
    }

    private async Task<RemoteLaneExecutorEntry?> ClaimWithSerializedKeysAsync(Check check,
        IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        string? busyKey = null;
        lock (_gate)
        {
            foreach (var entry in EligibleExecutors())
            {
                if (_arbiter.YieldReason(entry.Id, _configuration.FocusedEvidence) is not null) continue;
                var claim = RemoteExecutorSerializedKeyClaim.TryClaim(_occupancyRoot, entry.Id, keys, out var busy);
                if (claim is null) { busyKey ??= busy; continue; }
                if (TryClaimOn(entry.Id) is { } claimed)
                {
                    _keyClaims.Add(check.Name, claim);
                    return claimed;
                }
                claim.Dispose();
            }
        }
        var settings = _configuration.FocusedEvidence;
        if (settings.FaultReason is null && settings.Mode == "shadow" && settings.ShadowPreemption == "cancellable")
        {
            RemoteLaneExecutorEntry[] candidates;
            lock (_gate) candidates = EligibleExecutors().ToArray();
            foreach (var entry in candidates)
            {
                IDisposable? claim = RemoteExecutorSerializedKeyClaim.TryClaim(_occupancyRoot, entry.Id, keys, out var busy);
                if (claim is null) { busyKey ??= busy; continue; }
                try
                {
                    RemoteExecutorSlotArbiter.Target? target;
                    lock (_gate) target = _arbiter.RequestPreemption([entry.Id], check.Name);
                    if (target is null) continue;
                    // Hold the keys throughout the slot wait: a busy key never enters that wait.
                    if (await _arbiter.WaitForGateAsync(target, _pollInterval, TryClaimOn,
                        (released, waited) => Record(CreateRequest(check, ""), RemoteLaneOutcomeCode.ShadowPreemptRequested,
                            reason: $"shadow_attempt={target.ShadowAttempt} executor={target.Executor} released={released.ToString().ToLowerInvariant()} waited_ms={waited}"),
                        cancellationToken).ConfigureAwait(false) is { } claimed)
                    {
                        lock (_gate) _keyClaims.Add(check.Name, claim);
                        claim = null;
                        return claimed;
                    }
                }
                finally { claim?.Dispose(); }
            }
        }
        RecordNoSlot(check, busyKey);
        _arbiter.WriteDemand(_configuration.Executors.Where(entry => entry.AdmissionEnabled).Select(entry => entry.Id));
        return null;
    }

    internal bool TryTakePreemption(string executorId) => _arbiter.TryTakePreemption(executorId);
    internal void CompletePreemption(string executorId) => _arbiter.CompletePreemption(executorId);

    internal RemoteLaneExecutorEntry? TryClaimIdleExecutor(Check check)
    {
        if (TryClaimIdleExecutor() is { } entry) return entry;
        RecordNoSlot(check);
        return null;
    }

    private void RecordNoSlot(Check check, string? busyKey = null)
    {
        RemoteLaneRequest request;
        IReadOnlyList<RemoteExecutorOccupancyEntry> occupancy;
        lock (_gate)
        {
            request = CreateRequest(check, "");
            if (!_noSlotLanes.Add(request.Lane)) return;
            occupancy = RemoteExecutorOccupancyCensus.Take(_occupancyRoot,
                _configuration.Executors.Select(executor => (executor.Id, !executor.AdmissionEnabled ? "disabled" : _retired.Contains(executor.Id) ? "retired" :
                    _busyUntil.TryGetValue(executor.Id, out var until) && until > _clock.GetUtcNow() ? "backoff" : "full")));
        }
        Record(request, RemoteLaneOutcomeCode.OfferSkippedNoExecutor,
            reason: (busyKey is null ? "" : $"serialized-key-busy={busyKey} ") +
                (_configuration.Executors.Any(entry => entry.AdmissionEnabled)
                    ? RemoteExecutorOccupancyCensus.Reason(occupancy) : "no-enabled-executor"), occupancy: occupancy);
    }

    internal void Release(string executorId, string? lane = null)
    {
        lock (_gate)
        {
            if (lane is not null && _keyClaims.Remove(lane, out var keyClaim)) keyClaim.Dispose();
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
        TimeSpan timeout, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var occupancy = Occupancy;
        var request = CreateRequest(check, entry.Id);
        RemoteLaneHistory.TryGetValue((request.Lane, request.FilterHash), out var history);
        var budget = RemoteLaneTestHostBudget.Decide(timeout, history);
        request = request with { TestHostTimeoutSeconds = budget.Seconds, TestHostBudget = budget };
        var submittedAt = _clock.GetUtcNow();
        IRemoteLaneHandle? handle = null;
        RemoteLaneResult? observedResult = null;
        var inspectingTrx = false;
        IReadOnlyList<RemoteLaneStep> steps = [];
        string? exceptionMessage = null;
        RemoteLaneLeaseDecision? leaseDecision = null;
        RemoteLaneRunnerLogCaptureTiming? captureTiming = null;
        try
        {
            var submission = await _executor.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            handle = submission.Handle;
            steps = submission.Steps ?? [];
            var refusal = submission.FailureReason switch
            {
                "executor-busy" => RemoteLaneOutcomeCode.ExecutorBusy,
                "executor-tools-mismatch" => RemoteLaneOutcomeCode.ExecutorToolsMismatch,
                "executor-tools-unverified" => RemoteLaneOutcomeCode.ExecutorToolsUnverified,
                _ => (RemoteLaneOutcomeCode?)null
            };
            if (handle is null && refusal is { } code)
            {
                lock (_gate)
                {
                    _busyUntil[entry.Id] = _clock.GetUtcNow().AddSeconds(entry.LeaseSeconds);
                    Release(entry.Id, check.Name);
                }
                Record(request, code, reason: submission.FailureReason, attempt: Detail());
                return new(request, null, null);
            }
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
                if (now - submittedAt > deadline) return await Fallback(RemoteLaneOutcomeCode.LaneTimeout).ConfigureAwait(false);
                var heartbeatAge = now - heartbeat;
                if (heartbeatAge > TimeSpan.FromSeconds(entry.LeaseSeconds))
                {
                    leaseDecision = new(now, submittedAt, heartbeat, heartbeatAge, entry.LeaseSeconds,
                        request.ExecutorId, entry.RunnerAlias, request.Lane, request.AttemptId, Snapshot());
                    return await Fallback(RemoteLaneOutcomeCode.LeaseExpired).ConfigureAwait(false);
                }
                occupancy?.Beat(request.Lane, heartbeat, now);
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
                        var reason = RemoteLaneRedEvidence.Retain(result.TestResultPaths, _attemptPrefix,
                            entry.Id, check.Name, result.ExitCode);
                        var red = decision.FailedPredicate is AcceptanceShardCompletionPredicates.FailingTrx or
                            AcceptanceShardCompletionPredicates.AssemblyCleanupFailure or AcceptanceShardCompletionPredicates.NonzeroExit;
                        return await Fallback(red ? RemoteLaneOutcomeCode.RemoteRed : RemoteLaneOutcomeCode.TrxIncomplete, result, reason: reason).ConfigureAwait(false);
                    }
                    if (check.ExclusiveResourceKeys.Count > 0 && trx.NotExecutedTestCount is not 0)
                    {
                        var projectDirectory = string.IsNullOrWhiteSpace(check.Project) ? null :
                            Path.GetDirectoryName(Path.GetFullPath(Path.Combine(_worktreePath, check.Project)));
                        var skips = RemoteLaneNotExecutedDecision.Decide(trx.NotExecutedTestCount,
                            RemoteLaneNotExecutedIdentityReader.Read(result.TestResultPaths),
                            DeclaredStaticSkipScanner.Scan(projectDirectory));
                        if (!skips.Accept)
                        {
                            try { _onEvent?.Invoke($"REMOTE_LANE_NOT_EXECUTED lane={check.Name} first_undeclared={skips.FirstUndeclared}"); }
                            catch { }
                            return await Fallback(RemoteLaneOutcomeCode.UnexpectedNotExecuted, result,
                                reason: $"not_executed={trx.NotExecutedTestCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}").ConfigureAwait(false);
                        }
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
                    foreach (var path in paths)
                        accepted = NormalizedGateResultStep.Apply(accepted, path, NormalizedGateResultStep.TrxSourceLabel);
                    inspectingTrx = false;
                    var detail = Detail();
                    var remoteDuration = ResolveRemoteDuration(detail.LastStatus, _clock.GetUtcNow() - submittedAt);
                    Record(request, RemoteLaneOutcomeCode.Accepted, result, attempt: detail);
                    Release(entry.Id, check.Name);
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
        finally
        {
            occupancy?.End(request.Lane, _clock.GetUtcNow());
        }

        async Task<RemoteLaneOutcome> Fallback(RemoteLaneOutcomeCode code, RemoteLaneResult? result = null, string? reason = null)
        {
            KeyValuePair<int, IDisposable>? slot;
            IDisposable? keyClaim;
            lock (_gate)
            {
                _retired.Add(entry.Id);
                slot = TakeHighestSlot(entry.Id);
                _keyClaims.Remove(check.Name, out keyClaim);
            }
            if (observedResult is null && result is null && handle is IRemoteLaneQueuedJobCancellation cancellation)
                try { cancellation.RequestQueuedJobCancellation(); }
                catch (Exception) { /* Best effort; cannot change fallback. */ }
            RemoteLaneRunnerLogCapture? capture = null;
            if (handle is IRemoteLaneAttemptDiagnosticsSource source)
            {
                var captureStartedAt = _clock.GetUtcNow();
                try
                {
                    using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    bound.CancelAfter(TimeSpan.FromSeconds(30));
                    // The source bounds its transport and file reads and returns after its cleanup.
                    capture = await source.CaptureRunnerLogAsync(bound.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* Diagnostics cannot change fallback. */ }
                finally { captureTiming = new(captureStartedAt, _clock.GetUtcNow()); }
            }
            keyClaim?.Dispose();
            if (slot is { } held)
            {
                RemoteExecutorOccupancy.Release(_occupancyRoot, entry.Id, _cache.AttemptId, held.Key);
                held.Value.Dispose();
            }
            Record(request, code, result, reason, Detail(capture));
            return new(request, handle, null);
        }

        RemoteLaneHandleDiagnostics? Snapshot()
        {
            try
            {
                var snapshot = (handle as IRemoteLaneAttemptDiagnosticsSource)?.Snapshot();
                return snapshot is null ? null : new(snapshot.Poll,
                    Array.AsReadOnly(snapshot.Fetches.ToArray()), snapshot.LastStatus?.Clone());
            }
            catch (Exception) { /* Optional diagnostics cannot change the decision. */ }
            return null;
        }

        RemoteLaneAttemptDetail Detail(RemoteLaneRunnerLogCapture? capture = null)
        {
            var snapshot = leaseDecision is null ? Snapshot() : leaseDecision.Snapshot;
            return new(capture?.FailedStep is { } failed ? steps.Concat([failed]).ToArray() : steps,
                snapshot?.Poll, snapshot?.Fetches ?? [], snapshot?.LastStatus, capture?.Path, exceptionMessage,
                leaseDecision, captureTiming);
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
        string? reason = null, RemoteLaneAttemptDetail? attempt = null,
        IReadOnlyList<RemoteExecutorOccupancyEntry>? occupancy = null)
    {
        var usage = RemoteLaneResourceUsage.TryRead(attempt?.LastStatus);
        RemoteExecutorHealthLedger.Append(_ledgerPath, new(_clock.GetUtcNow(), request.ExecutorId,
            request.AttemptId, request.Lane, code,
            code is RemoteLaneOutcomeCode.Accepted or RemoteLaneOutcomeCode.NotEligibleExclusiveResource or
                RemoteLaneOutcomeCode.ExecutorBusy or RemoteLaneOutcomeCode.OfferSkippedNoExecutor or
                RemoteLaneOutcomeCode.ExecutorToolsMismatch or RemoteLaneOutcomeCode.ShadowPreemptRequested ? null : "executor",
            new(request.ExecutorId, request.Lane, request.VerifyingCommitSha, request.CandidateTreeSha,
                request.MainSha, request.FilterHash, request.ManifestIdentity),
            result is null ? null : new(result.ExecutorId, result.Lane, result.VerifyingCommitSha, result.ObservedTreeSha,
                result.MainSha, result.FilterHash, result.ManifestIdentity), reason, Attempt: attempt, Occupancy: occupancy,
            RemoteTestHostCpuSeconds: usage?.TestHostCpuSeconds, RemoteTestHostPeakWorkingSetBytes: usage?.TestHostPeakWorkingSetBytes,
            RemoteAvailableMemoryMbMin: usage?.AvailableMemoryMbMin, RemoteResourceSamples: usage?.ResourceSamples,
            Phases: attempt is null ? null : RemoteLanePhaseTimings.Compute(
                attempt.Steps, attempt.LastStatus, result?.TestResultPaths, attempt.Fetches),
            TestHostBudget: request.TestHostBudget));
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
        _arbiter.Dispose();
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
            foreach (var claim in _keyClaims.Values) claim.Dispose();
            _keyClaims.Clear();
        }
    }
}
