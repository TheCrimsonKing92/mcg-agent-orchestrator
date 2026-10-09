using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorPolicyResolution(
    ConductorAutonomyPolicy Policy,
    string Source,
    IReadOnlyList<string> Warnings);

internal readonly record struct ParallelAcceptanceOldestWaiterObservation(
    Goal? Waiter,
    int ConsecutiveTicks)
{
    internal bool IsStalled =>
        Waiter is not null &&
        ConsecutiveTicks >= ConductorBatchLoop.ParallelAcceptanceOldestWaiterStallTickThreshold;
}

internal sealed partial class ConductorBatchLoop
{
    internal const string StopFileName = ".conduct-stop";
    internal const int DefaultMaxVerifyRetries = 2;
    internal const int DefaultWatchIntervalSeconds = 15;
    internal const int DefaultBlockedRecheckCycles = 2;
    internal static readonly TimeSpan DefaultBlockedRecheckHeartbeatInterval = TimeSpan.FromMinutes(5);
    internal const int WatchStopPollIntervalSeconds = 5;
    internal const int QuietSummaryEveryTicks = 20;
    internal const int DefaultMaxBusyWriteAttempts = 1;
    internal const int DispatchRecordContentionSkipLimit = 5;
    internal const int SlotContentionAttentionHoldLimit = 5;
    internal const int DefaultGracefulDetachCheckpointAttempts = 3;
    internal const int JanitorialFailureEscalationThreshold = 3;
    internal const int ParallelAcceptanceTransientFailureCap = 3;
    internal const int ParallelAcceptanceBoundedOvertakeLimit = 1;
    // Tick 1 permits the single bounded overtake, tick 2 preserves the oldest waiter's
    // guaranteed turn, and tick 3 is the first bypass. A waiter with a live attempt is
    // excluded by the selector; admission on the threshold tick sets the served guard.
    internal const int ParallelAcceptanceOldestWaiterStallTickThreshold = 3;
    // Parallel-acceptance WIDTH governs logical, per-tick landing-gate admission. BuildConcurrencySlotCount
    // separately governs isolated build environments and their OS file-lock permits, which a gate holds only
    // for its shared prebuild. Both defaults remain 2 by current tuning, not by derivation. Before raising width,
    // measure intermittent acceptance-gate failures per attempt stratified by peak concurrent gate count, plus
    // p95 Verified-to-gate-start time and peak CPU, working set, and disk-queue depth; widen the slot-heartbeat
    // enumeration first if width will exceed build slots. The maximum rejects corrupt overrides, not endorses 4.
    internal const int DefaultParallelAcceptanceCapacity = 2;
    internal const int MaxParallelAcceptanceCapacity = 4;
    // Paid-worker ADMISSION pool that the gate-slot reservation (ConductorDriver worker-cap)
    // draws from. Deliberately INDEPENDENT of build concurrency / acceptance width: coding
    // workers do not hold build slots. On the 47.9-GiB operator host, a reviewed paid-worker
    // dispatch peaked at 1,750,343,680 bytes. Limiting workers to one third of physical memory
    // yields floor(51,385,864,192 / 3 / 1,750,343,680) = 9, leaving two thirds for the OS,
    // conductor, builds, and acceptance gates (observed 2026-08-08). Provider cooldowns are
    // handled separately; revisit this fixed limit when memory-aware admission is implemented.
    // Do NOT tie this to BuildConcurrencySlotCount or DefaultParallelAcceptanceCapacity.
    internal const int WorkerAdmissionCapacity = 9;
    internal const int DefaultUnscopedStallTickThreshold = 3;
    internal static readonly TimeSpan DefaultGoalStallThreshold = TimeSpan.FromMinutes(10);
    internal const string SelfRelaunchEnabledEnvironmentVariable = "MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED";
    internal const bool DefaultSelfRelaunchEnabled = true;
    internal const string SetAsideSelfClearDecisionPrefix = "Set-aside self-cleared:";
    private readonly Func<AgentOrchestratorKernel, IReadOnlySet<string>, TerminalGoalSweepResult?> _sweep;
    private readonly Action<AgentOrchestratorKernel, Goal> _reapGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel, Goal> _detachGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel> _recoverInterruptedDispatches;
    private readonly GoalDispatchRefresh _refreshGoalDispatchesBeforeAdvance;
    private readonly ConductorWatchProgressReporter _watchProgressReporter;
    private readonly OperatorIntentCoordinator? _operatorIntents;
    private readonly ProgressiveReviewGlanceCoordinator? _progressiveReviewGlances;
    private readonly ProgressiveReviewSteeringCoordinator? _progressiveReviewSteering;
    private readonly Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? _handoffOnMaxDuration;
    private readonly Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? _selfRelaunch;
    private readonly bool _selfRelaunchEnabled;
    private readonly PostLandingCanaryCoordinator? _postLandingCanary;
    private readonly PromptRolloutWatchCoordinator? _promptRolloutWatch;
    private readonly AcceptanceEngineCircuitBreaker? _acceptanceEngineCircuit;
    private readonly ConductEventLogWriter? _conductEventLogWriter;
    private readonly ConductorLifecycleRecorder? _lifecycleRecorder;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string?> _readRelaunchDrainCap;
    private readonly Func<double> _writeJitter;
    private readonly Func<string, ConductorGoalReloadObservation> _goalReloadObservation;
    private readonly TimeSpan _blockedRecheckHeartbeatInterval;
    private readonly OrchestratorWorkspace? _workspace;
    // Janitorial phases run only on the conductor loop thread; acceptance work never mutates this state.
    private readonly Dictionary<string, int> _consecutiveJanitorialFailures = new(StringComparer.Ordinal);
    private readonly ConductorSlotContentionHolds _slotContentionHolds = new();
    private readonly Dictionary<string, long> _tickPhaseElapsedMs = new(StringComparer.Ordinal);
    private readonly Action<string>? _janitorialPhaseProbe;
    private readonly Func<long> _janitorialTimestamp;
    private static readonly AsyncLocal<ConductEventLogWriter?> CurrentConductEventLogWriter = new();
    private static readonly AsyncLocal<RetryDiagnosticCoalescer?> CurrentRetryDiagnostics = new();

    public ConductorBatchLoop(
        Action<AgentOrchestratorKernel>? sweep = null,
        Action<AgentOrchestratorKernel, Goal>? reapGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel, Goal>? detachGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel>? recoverInterruptedDispatches = null,
        GoalDispatchRefresh? refreshGoalDispatchesBeforeAdvance = null,
        ConductorWatchProgressReporter? watchProgressReporter = null,
        Func<AgentOrchestratorKernel, TerminalGoalSweepResult?>? measuredSweep = null,
        Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? handoffOnMaxDuration = null,
        ConductEventLogWriter? conductEventLogWriter = null,
        Func<DateTimeOffset>? utcNow = null,
        OperatorIntentCoordinator? operatorIntents = null,
        ProgressiveReviewGlanceCoordinator? progressiveReviewGlances = null,
        ProgressiveReviewSteeringCoordinator? progressiveReviewSteering = null,
        Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? selfRelaunch = null,
        bool selfRelaunchEnabled = DefaultSelfRelaunchEnabled,
        PostLandingCanaryCoordinator? postLandingCanary = null,
        AcceptanceEngineCircuitBreaker? acceptanceEngineCircuit = null,
        Func<string, ConductorGoalReloadObservation>? goalReloadObservation = null,
        ConductorLifecycleRecorder? lifecycleRecorder = null,
        Func<double>? writeJitter = null,
        TimeSpan? blockedRecheckHeartbeatInterval = null,
        Func<AgentOrchestratorKernel, IReadOnlySet<string>, TerminalGoalSweepResult?>? measuredSweepWithCheckpointHolds = null,
        OrchestratorWorkspace? workspace = null,
        Action<string>? janitorialPhaseProbe = null,
        Func<long>? janitorialTimestamp = null,
        Func<string?>? readRelaunchDrainCap = null,
        PromptRolloutWatchCoordinator? promptRolloutWatch = null,
        Func<TimeSpan>? processCpuTime = null)
    {
        _sweep = measuredSweepWithCheckpointHolds is not null
            ? measuredSweepWithCheckpointHolds
            : measuredSweep is not null
                ? (kernel, _) => measuredSweep(kernel)
                : (kernel, _) => InvokeLegacySweep(kernel);
        TerminalGoalSweepResult? InvokeLegacySweep(AgentOrchestratorKernel kernel)
        {
            sweep?.Invoke(kernel);
            return null;
        }
        _reapGoalRunningDispatches = reapGoalRunningDispatches ?? ((_, _) => { });
        _detachGoalRunningDispatches = detachGoalRunningDispatches ?? _reapGoalRunningDispatches;
        _recoverInterruptedDispatches = kernel => { StaleDispatchProcessReconciler.Reconcile(kernel); recoverInterruptedDispatches?.Invoke(kernel); };
        _refreshGoalDispatchesBeforeAdvance = refreshGoalDispatchesBeforeAdvance ?? ((_, _) => null);
        _watchProgressReporter = watchProgressReporter ?? new ConductorWatchProgressReporter();
        _operatorIntents = operatorIntents;
        _progressiveReviewGlances = progressiveReviewGlances;
        _progressiveReviewSteering = progressiveReviewSteering;
        _handoffOnMaxDuration = handoffOnMaxDuration;
        _selfRelaunch = selfRelaunch;
        _selfRelaunchEnabled = selfRelaunchEnabled;
        _postLandingCanary = postLandingCanary;
        _acceptanceEngineCircuit = postLandingCanary?.CircuitBreaker ?? acceptanceEngineCircuit;
        _conductEventLogWriter = conductEventLogWriter;
        _lifecycleRecorder = lifecycleRecorder;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _processCpuTime = processCpuTime ?? (() => Environment.CpuUsage.TotalTime);
        _readRelaunchDrainCap = readRelaunchDrainCap ?? (() => Environment.GetEnvironmentVariable(RelaunchDrainCapEnvironmentVariable));
        _writeJitter = writeJitter ?? Random.Shared.NextDouble;
        _goalReloadObservation = goalReloadObservation ?? (_ => new ConductorGoalReloadObservation.Missing());
        _blockedRecheckHeartbeatInterval = blockedRecheckHeartbeatInterval ?? DefaultBlockedRecheckHeartbeatInterval;
        _workspace = workspace;
        _promptRolloutWatch = promptRolloutWatch ?? PromptRolloutWatchCoordinator.CreateDefault(workspace, line => EmitProgress(line));
        _janitorialPhaseProbe = janitorialPhaseProbe;
        _janitorialTimestamp = janitorialTimestamp ?? Stopwatch.GetTimestamp;
        if (_blockedRecheckHeartbeatInterval <= TimeSpan.Zero || _blockedRecheckHeartbeatInterval > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(blockedRecheckHeartbeatInterval));
        }
    }

    public BatchLoopSummary Run(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        string stopFilePath,
        int? maxIterations = null,
        int maxVerifyRetries = DefaultMaxVerifyRetries,
        TimeSpan? watchInterval = null,
        Action<BatchTickSummary>? onTick = null,
        Func<TimeSpan, bool>? sleepFunc = null,
        IConductorWakeSignal? wakeSignal = null,
        TimeSpan? maxDuration = null,
        string? onlyGoalId = null,
        Action<AgentOrchestratorKernel>? persistTick = null,
        bool keepAliveWhenIdle = false,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick = null,
        Func<AgentOrchestratorKernel, IReadOnlyList<ConductorOperatorDispositionSnapshot>>? buildOperatorDispositions = null,
        bool quiet = false,
        TimeSpan? stallWarningThreshold = null,
        TimeSpan? goalStallThreshold = null,
        int unscopedStallTickThreshold = DefaultUnscopedStallTickThreshold,
        Action<TimeSpan>? busyWriteDelay = null,
        string? journalMode = null,
        string policySource = "preset",
        Func<ConductorPolicyResolution>? reloadPolicy = null,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick = null,
        Func<bool>? hasTransientLoadHold = null,
        TimeSpan? maxDurationDeferralCeiling = null,
        Action<bool>? onMaxDurationDeferralStateChanged = null,
        bool emitActivationHeartbeat = false)
    {
        var leaseDirectory = Path.GetDirectoryName(Path.GetFullPath(stopFilePath)) ?? Directory.GetCurrentDirectory();
        var leaseAcquisition = AcquireActiveDatabaseLeases(leaseDirectory, busyWriteDelay);
        if (!leaseAcquisition.Succeeded)
            return leaseAcquisition.DeferredSummary!;

        using var activeConductorLease = leaseAcquisition.StateLease!;
        using var activeRunEventLease = leaseAcquisition.RunEventLease!;
        checkpointGoalTick = ResetLandingTickSave(checkpointGoalTick);
        _consecutiveJanitorialFailures.Clear();
        _slotContentionHolds.Clear();
        ResetWorkerCapacityWatch();
        var previousConductEventLogWriter = leaseAcquisition.PreviousConductEventLogWriter;
        var previousRetryDiagnostics = CurrentRetryDiagnostics.Value;
        var previousSuccessfulLandingSink = driver.SuccessfulLandingSink;
        var previousDispatchRecordWriteSucceededSink = driver.DispatchRecordWriteSucceededSink;
        var previousLandingMutationBlocker = driver.LandingMutationBlocker;
        var canaryTasks = new List<Task<PostLandingCanaryDisposition>>();
        var canaryTasksGate = new object();
        CurrentRetryDiagnostics.Value = new RetryDiagnosticCoalescer(_utcNow);
        var totalTicks = 0;
        var blockedRecheckCycles = 0;
        var totalBlockedRechecks = 0;
        var dispatchRecordWriteSkips = new Dictionary<string, int>(StringComparer.Ordinal);
        var checkpointHeldGoals = new Dictionary<string, GoalSnapshotCheckpointResult>(StringComparer.Ordinal);
        var blockedRecheckRecurrences = new Dictionary<string, BlockedRecheckRecurrence>(StringComparer.Ordinal);
        DateTimeOffset? lastBlockedRecheckHeartbeatAt = null;
        string? stopReason = null;
        ConductorLifecycleSession? lifecycleSession = null;

        void StopLoop(string reason, string? detail = null)
        {
            stopReason ??= reason;
            lifecycleSession?.Stop(reason, totalTicks, detail);
            EmitProgress($"LOOP_STOP tick={totalTicks} rechecks={totalBlockedRechecks} reason={reason}" +
                         (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}"));
        }
        void CompleteActivationTick(int tick) => EmitActivationTickEnd(emitActivationHeartbeat, tick);

        driver.DispatchRecordWriteSucceededSink = goalId =>
        {
            dispatchRecordWriteSkips.Remove(goalId.Value);
            previousDispatchRecordWriteSucceededSink?.Invoke(goalId);
        };
        driver.LandingMutationBlocker = () =>
        {
            var existingBlock = previousLandingMutationBlocker?.Invoke();
            if (!string.IsNullOrWhiteSpace(existingBlock))
            {
                return existingBlock;
            }

            var snapshot = (_acceptanceEngineCircuit ?? _postLandingCanary?.CircuitBreaker)?.Read();
            if (snapshot is null)
            {
                return null;
            }

            var decision = AcceptanceEngineAcceptanceGate.Decide(
                snapshot.Health,
                AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
            return decision.Allowed
                ? null
                : $"{decision.Reason}; landing={snapshot.LandingSha ?? "unknown-sha"}; " +
                  $"failure={snapshot.FailureReason ?? "canary-pending"}";
        };
        try
        {
        var excludedGoals = new HashSet<string>(StringComparer.Ordinal);
        var setAsideGoals = new Dictionary<string, BatchSetAsideEntry>(StringComparer.Ordinal);
        var selfClearedSetAsideEntries = new Dictionary<string, BatchSetAsideEntry>(StringComparer.Ordinal);
        var readmittedRetryReservations = new HashSet<string>(StringComparer.Ordinal);
        var completedGoals = new HashSet<string>(StringComparer.Ordinal);
        var escalatedGoals = new HashSet<string>(StringComparer.Ordinal);
        var dependencyEscalatedGoals = new HashSet<string>(StringComparer.Ordinal);
        var advanceFaultRetries = new HashSet<(string GoalId, string Fingerprint)>();
        var reapedGoals = new HashSet<string>(StringComparer.Ordinal);
        var retryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastGoalDisposition = new Dictionary<string, string>(StringComparer.Ordinal);
        var unscopedDispatchableTicks = new Dictionary<string, int>(StringComparer.Ordinal);
        var goalProjectionCache = new GoalProjectionCache();
        var clampedRechecks = new HashSet<RetryDiagnosticKey>();
        var totalAdvanced = 0;
        var totalHeld = 0;
        var totalEscalated = 0;
        var totalRetried = 0;
        var totalDone = 0;
        var landedGoalIds = new HashSet<string>(StringComparer.Ordinal);
        var stopRequested = false;
        var maxDurationReached = false;
        var isDeferringMaxDurationStop = false;
        var maxDurationDeferralAnnounced = false;
        DateTimeOffset? maxDurationDeferralStartedAt = null;
        HashSet<string>? maxDurationDeferredAttemptIds = null;
        ConductorSelfRelaunchRequest? pendingSelfRelaunch = null;
        ConductorSelfRelaunchRequest? deferredSelfRelaunch = null;
        int? selfRelaunchRetryAfterTick = null;
        DateTimeOffset? selfRelaunchDrainStartedAt = null;
        var selfRelaunchDrainCap = new SelfRelaunchDrainCapState(
            ResolveRelaunchDrainCap(_readRelaunchDrainCap()));
        ConductorLoopHandoffResult? selfRelaunchHandoff = null;
        var started = _utcNow();
        _cohortGatherDeadline = ComputeCohortGatherDeadline(started, maxDuration);
        var effectiveGoalStallThreshold = goalStallThreshold ?? DefaultGoalStallThreshold;
        if (effectiveGoalStallThreshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(goalStallThreshold));
        }
        lifecycleSession = _lifecycleRecorder?.Start(
            policy.Name,
            onlyGoalId,
            started,
            maxIterations,
            maxDuration);

        TimeSpan ConsumeWatchInterval(TimeSpan configuredInterval, int recheckableBlockedGoals)
        {
            var computedInterval = GetWatchFallbackInterval(kernel, onlyGoalId, configuredInterval);
            if (recheckableBlockedGoals <= 0)
            {
                return computedInterval;
            }

            var effectiveInterval = RetryLoopPolicy.ClampInterval(
                computedInterval,
                TimeSpan.FromSeconds(WatchStopPollIntervalSeconds));
            if (effectiveInterval == computedInterval)
            {
                return effectiveInterval;
            }

            foreach (var entry in setAsideGoals.Values.Where(entry =>
                         (onlyGoalId is null || entry.GoalId == onlyGoalId) &&
                         kernel.Goals.Any(goal =>
                             goal.Id.Value == entry.GoalId &&
                             !IsTerminalGoal(goal))))
            {
                var key = new RetryDiagnosticKey(
                    "BLOCKED_RECHECK_INTERVAL_CLAMPED",
                    ShortGoalId(entry.GoalId),
                    entry.Condition.ToString().ToLowerInvariant());
                if (clampedRechecks.Add(key))
                {
                    EmitProgress(
                        $"BLOCKED_RECHECK_INTERVAL_CLAMPED goal={key.Goal} condition={key.Condition} computedSeconds={computedInterval.TotalSeconds:0.###} floorSeconds={WatchStopPollIntervalSeconds}");
                }
            }

            return effectiveInterval;
        }
        var initiallyCompletedGoalIds = GetCompletedGoalIds(kernel);
        driver.SuccessfulLandingSink = receipt =>
        {
            var landedAt = _promptRolloutWatch is null ? default : _utcNow();
            RecordSuccessfulLanding(landedGoalIds, receipt.GoalId);
            var decision = RepositoryChangeClassifier.DecideConductorRelaunch(receipt.ChangedFiles);
            if (decision.Required && _selfRelaunchEnabled && _selfRelaunch is not null)
            {
                selfRelaunchDrainCap.NoteLanding(receipt.ChangedFiles);
                pendingSelfRelaunch = new ConductorSelfRelaunchRequest(receipt.GoalId, totalTicks);
                deferredSelfRelaunch = null;
                selfRelaunchRetryAfterTick = null;
                selfRelaunchDrainStartedAt ??= _utcNow();
                EmitProgress(
                    $"LOOP_RELAUNCH_SCHEDULED tick={totalTicks} goal={receipt.GoalId} " +
                    $"changedFiles={receipt.ChangedFiles.Count} coalesced=true");
            }
            else
            {
                var reason = decision.Required
                    ? (_selfRelaunchEnabled ? "self-relaunch-unavailable" : "self-relaunch-disabled") +
                      $"+{decision.Classification}"
                    : decision.Classification;
                EmitRelaunchNotRequired(totalTicks, receipt, decision with { Required = false, Classification = reason });
            }

            NoteLandingAndStopSupersededAttempts(kernel, driver, receipt.GoalId);
            if (_postLandingCanary is not null)
            {
                var canaryTask = _postLandingCanary.LaunchLandingAsync(receipt);
                lock (canaryTasksGate)
                {
                    canaryTasks.Add(canaryTask);
                }
            }

            _promptRolloutWatch?.NoteLanding(receipt, landedAt);
            previousSuccessfulLandingSink?.Invoke(receipt);
        };
        var workerAdmission = driver.GetWorkerAdmissionSnapshot(policy);
        EmitProgress(
            $"LOOP_START policy={Sanitize(policy.Name)} policySource={SanitizeReason(policySource)} maxIterations={maxIterations?.ToString() ?? "none"} " +
            $"maxDurationSeconds={(maxDuration.HasValue ? ((int)maxDuration.Value.TotalSeconds).ToString() : "none")} " +
            $"configuredWorkerCap={workerAdmission.ConfiguredWorkerCap} workerAdmissionCapacity={workerAdmission.AdmissionCapacity} " +
            $"reservedGateSlots={workerAdmission.ReservedGateSlots} effectiveWorkerCap={workerAdmission.EffectiveWorkerCap} " +
            $"acceptanceWidth={policy.AcceptanceWidth}" +
            (string.IsNullOrWhiteSpace(journalMode) ? string.Empty : $" journalMode={Sanitize(journalMode)}"));

        SelfRelaunchDrainOutcome DrainSelfRelaunch(int tick)
        {
            var activeDispatches = RunJanitorialPhase<int?>(
                "count-running-dispatches",
                tick,
                () => CountRunningDispatches(kernel, onlyGoalId));
            if (!activeDispatches.HasValue)
            {
                return new(SelfRelaunchDrainDisposition.ContinueTick);
            }

            if (activeDispatches.Value > 0)
            {
                var drainElapsed = _utcNow() - (selfRelaunchDrainStartedAt ?? _utcNow());
                var capDecision = selfRelaunchDrainCap.Evaluate(
                    kernel, onlyGoalId, excludedGoals, reapedGoals, drainElapsed);
                if (capDecision.Detach)
                {
                    var detachedBefore = CountGracefullyDetachedRunningDispatches(kernel, onlyGoalId);
                    var capReapedGoals = new HashSet<string>(reapedGoals, StringComparer.Ordinal);
                    DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, capReapedGoals);
                    selfRelaunchDrainCap.Detached = true;
                    var detached = CountGracefullyDetachedRunningDispatches(kernel, onlyGoalId) - detachedBefore;
                    EmitProgress($"LOOP_RELAUNCH_DETACH tick={totalTicks} goal={pendingSelfRelaunch!.GoalId} detached={detached} capMinutes={selfRelaunchDrainCap.Cap.TotalMinutes:0} admitting=false");
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "self-relaunch-detach", null,
                        busyWriteDelay, checkpointGoalTick, checkpointHeldGoals);
                }
                if (!capDecision.Detach && !capDecision.AlreadyDetached)
                {
                    if (drainElapsed >= DispatchRecoveryPolicy.DefaultLiveIdleTimeout)
                    {
                        EmitSelfRelaunchRollback(totalTicks, pendingSelfRelaunch!.GoalId, "drain",
                            $"active dispatches did not reach terminal receipts within {(int)DispatchRecoveryPolicy.DefaultLiveIdleTimeout.TotalMinutes} minutes");
                        deferredSelfRelaunch = pendingSelfRelaunch;
                        selfRelaunchRetryAfterTick = totalTicks + 1;
                        pendingSelfRelaunch = null;
                        selfRelaunchDrainStartedAt = null;
                    }

                    if (pendingSelfRelaunch is not null)
                    {
                        EmitProgress(
                            $"LOOP_RELAUNCH_DRAIN tick={totalTicks} goal={pendingSelfRelaunch.GoalId} active={activeDispatches.Value} admitting=false{capDecision.DrainSuffix}");
                        TryPersistCheckpoint(
                            persistTick,
                            persistGoalTick,
                            kernel,
                            totalTicks,
                            onlyGoalId,
                            "self-relaunch-drain",
                            null,
                            busyWriteDelay,
                            checkpointGoalTick: checkpointGoalTick,
                            checkpointHeldGoalIds: checkpointHeldGoals);
                        var drainWait = TimeSpan.FromSeconds(WatchStopPollIntervalSeconds);
                        if (sleepFunc is not null)
                        {
                            sleepFunc(drainWait);
                        }
                        else
                        {
                            SleepUntilNextTick(
                                drainWait,
                                stopFilePath,
                                wakeSignal,
                                GetRunningDispatchExitCodePaths(kernel, onlyGoalId));
                        }
                        return new(SelfRelaunchDrainDisposition.ContinueTick);
                    }
                }
            }

            if (pendingSelfRelaunch is null)
            {
                return new(SelfRelaunchDrainDisposition.Proceed);
            }

            var canaryAwaited = RunJanitorialPhase<bool?>("await-canary-tasks", tick, () =>
            {
                AwaitCanaryTasks(canaryTasks, canaryTasksGate);
                return true;
            });
            if (canaryAwaited != true)
            {
                return new(SelfRelaunchDrainDisposition.ContinueTick);
            }

            EmitProgress(
                $"LOOP_RELAUNCH_REBUILD tick={totalTicks} goal={pendingSelfRelaunch.GoalId} active=0 admitting=false");
            ConductorSelfRelaunchResult relaunchResult;
            try
            {
                relaunchResult = _selfRelaunch!(pendingSelfRelaunch);
            }
            catch (Exception ex)
            {
                EmitProgress(
                    $"LOOP_HANDOFF_FAILED tick={totalTicks} goal={pendingSelfRelaunch.GoalId} phase=handoff " +
                    $"rolledBack=false continuing=false reason={SanitizeHandoffDetail($"{ex.GetType().Name}: {ex.Message}")}");
                return new(
                    SelfRelaunchDrainDisposition.Proceed,
                    new InvalidOperationException(
                        "Self-relaunch failed without confirming incumbent authority; refusing to continue the conductor loop.",
                        ex));
            }
            if (relaunchResult.HandedOff)
            {
                selfRelaunchHandoff = relaunchResult.Handoff;
                EmitHandoffProgress(totalTicks, relaunchResult.Handoff!, pendingSelfRelaunch.GoalId);
                return new(SelfRelaunchDrainDisposition.BreakLoop);
            }

            if (!relaunchResult.IncumbentCanContinue)
            {
                EmitProgress(
                    $"LOOP_HANDOFF_FAILED tick={totalTicks} goal={pendingSelfRelaunch.GoalId} phase=handoff " +
                    $"rolledBack=false continuing=false reason={SanitizeHandoffDetail(relaunchResult.Reason ?? "rollback authority was not confirmed")}");
                return new(
                    SelfRelaunchDrainDisposition.Proceed,
                    new InvalidOperationException(
                        "Self-relaunch rollback did not confirm incumbent authority; refusing to continue the conductor loop."));
            }

            if (string.Equals(relaunchResult.FailedPhase, "handoff", StringComparison.Ordinal))
            {
                EmitProgress(
                    $"LOOP_HANDOFF_FAILED tick={totalTicks} goal={pendingSelfRelaunch.GoalId} phase=handoff " +
                    $"rolledBack=true continuing=true reason={SanitizeHandoffDetail(relaunchResult.Reason ?? "unknown")}");
            }
            else
            {
                EmitSelfRelaunchRollback(
                    totalTicks,
                    pendingSelfRelaunch.GoalId,
                    relaunchResult.FailedPhase ?? "build",
                    relaunchResult.Reason ?? "unknown");
            }

            pendingSelfRelaunch = null;
            selfRelaunchDrainStartedAt = null;
            selfRelaunchDrainCap.Reset();
            return new(SelfRelaunchDrainDisposition.Proceed);
        }

        while (true)
        {
            using var writeOperationTag = SqliteOrchestratorStateRepository.UseWriteOperationTag("loop:tick");
            _tickPhaseElapsedMs.Clear();
            if (pendingSelfRelaunch is null &&
                deferredSelfRelaunch is not null &&
                totalTicks >= selfRelaunchRetryAfterTick)
            {
                pendingSelfRelaunch = deferredSelfRelaunch;
                deferredSelfRelaunch = null;
                selfRelaunchRetryAfterTick = null;
                selfRelaunchDrainStartedAt = _utcNow();
            }

            if (IsStopRequested(stopFilePath))
            {
                stopRequested = true;
                StopLoop("stop-file");
                Console.WriteLine($"[conduct --loop] Stop signal detected at tick {totalTicks + 1}; no new dispatches will be started.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                PersistGracefulDetachCheckpoint(
                    persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop", null, busyWriteDelay,
                    checkpointGoalTick, checkpointHeldGoals);
                break;
            }

            if (pendingSelfRelaunch is null &&
                maxIterations.HasValue &&
                totalTicks >= maxIterations.Value)
            {
                StopLoop("max-iter", $"max={maxIterations.Value}");
                Console.WriteLine($"[conduct --loop] Max iterations ({maxIterations.Value}) reached after {totalTicks} ticks.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                PersistGracefulDetachCheckpoint(
                    persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "max-iterations", null, busyWriteDelay,
                    checkpointGoalTick, checkpointHeldGoals);
                break;
            }

            if (pendingSelfRelaunch is null &&
                maxDuration.HasValue &&
                _utcNow() - started >= maxDuration.Value)
            {
                var now = _utcNow();
                maxDurationDeferralStartedAt ??= now;
                var snapshot = BuildMaxDurationAcceptanceSnapshot(kernel, driver, maxDurationDeferredAttemptIds);
                var verdict = ConductorMaxDurationStopDeferral.Decide(
                    now,
                    maxDurationDeferralStartedAt.Value,
                    snapshot.Attempts,
                    maxDurationDeferralCeiling ?? AcceptanceCheckTimeouts.DefaultTimeout,
                    snapshot.Failure);
                if (verdict.Kind == ConductorMaxDurationStopVerdictKind.Defer)
                {
                    isDeferringMaxDurationStop = true;
                    if (maxDurationDeferredAttemptIds is null && verdict.Attempts.Count > 0)
                        maxDurationDeferredAttemptIds = verdict.Attempts.Select(attempt => attempt.AttemptId).ToHashSet(StringComparer.Ordinal);
                    onMaxDurationDeferralStateChanged?.Invoke(true);
                    if (!maxDurationDeferralAnnounced)
                        EmitProgress(ConductorMaxDurationStopDeferral.FormatDeferredEvent(totalTicks, now, maxDurationDeferralStartedAt.Value, maxDurationDeferralCeiling ?? AcceptanceCheckTimeouts.DefaultTimeout, verdict.Attempts, snapshot.Failure));
                    maxDurationDeferralAnnounced = true;
                }
                else
                {
                    onMaxDurationDeferralStateChanged?.Invoke(false);
                    StopLoop("max-duration", ConductorMaxDurationStopDeferral.FormatStopDetail(maxDuration.Value, verdict, snapshot.Failure));
                    Console.WriteLine($"[conduct --loop] Max duration ({maxDuration.Value.TotalSeconds:0}s) reached after {totalTicks} ticks.");
                    DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "max-duration", null, busyWriteDelay,
                        checkpointGoalTick, checkpointHeldGoals);
                    maxDurationReached = true;
                    break;
                }
            }

            var nextTick = totalTicks + 1;
            using var tickStepLedger = BeginTickCpuAndStepLedger();
            if (reloadPolicy is not null)
            {
                try
                {
                    var reloaded = reloadPolicy();
                    if (!PoliciesMatch(policy, reloaded.Policy) ||
                        !string.Equals(policySource, reloaded.Source, StringComparison.Ordinal))
                    {
                        foreach (var warning in reloaded.Warnings)
                        {
                            EmitProgress($"POLICY_WARNING tick={nextTick} message={SanitizeReason(warning)}");
                        }

                        EmitProgress(
                            $"POLICY_RELOAD tick={nextTick} source={SanitizeReason(reloaded.Source)} " +
                            $"{FormatPolicyValues("old", policy)} {FormatPolicyValues("new", reloaded.Policy)}");
                        policy = reloaded.Policy;
                        policySource = reloaded.Source;
                    }
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
                {
                    EmitProgress(
                        $"POLICY_RELOAD_FAILED tick={nextTick} source={SanitizeReason(policySource)} " +
                        $"currentPolicy={Sanitize(policy.Name)} reason={SanitizeReason($"{ex.GetType().Name}: {ex.Message}")}");
                }
            }

            var preTickTimingLines = new List<string>();
            if (checkpointGoalTick is not null && checkpointHeldGoals.Count > 0)
            {
                var recoveryGoalIds = kernel.Goals
                    .Where(goal => checkpointHeldGoals.ContainsKey(goal.Id.Value))
                    .Select(goal => goal.Id)
                    .ToArray();
                if (recoveryGoalIds.Length > 0)
                {
                    ApplyCheckpointOutcomes(
                        checkpointGoalTick(kernel, recoveryGoalIds),
                        recoveryGoalIds,
                        checkpointHeldGoals,
                        nextTick,
                        "recovery",
                        preTickTimingLines,
                        deferEmission: true);
                }
            }
            var preSweepBaseline = GoalKernelChange.CaptureAll(kernel);
            TerminalGoalJournalMetadataCache.BeginMeasurement();
            var sweepClock = StartDiagnosticTimer();
            var sweepCpuStart = ReadProcessCpu();
            var sweepResult = RunJanitorialPhase(
                "sweep",
                nextTick,
                () => _sweep(kernel, checkpointHeldGoals.Keys.ToHashSet(StringComparer.Ordinal)));
            foreach (var sweepEvent in (sweepResult?.Events ?? []).Concat(sweepResult?.OwnedRoots?.OperatorEvents ?? []))
            {
                EmitProgress(sweepEvent);
            }
            var sweepTerminalizedGoalIds = RunJanitorialPhase(
                "persist-sweep-terminalizations",
                nextTick,
                () => PersistSweepTerminalizations(
                    sweepResult, kernel, checkpointGoalTick, persistGoalTick, checkpointHeldGoals, nextTick, preTickTimingLines, busyWriteDelay)) ?? [];
            RunJanitorialPhase("recover-interrupted-dispatches", nextTick, () =>
            {
                _recoverInterruptedDispatches(kernel);
                return true;
            });
            if (pendingSelfRelaunch is not null)
            {
                var drainOutcome = RunJanitorialPhase(
                    "self-relaunch-drain",
                    nextTick,
                    () => DrainSelfRelaunch(nextTick));
                if (drainOutcome?.Fault is not null)
                {
                    throw drainOutcome.Fault;
                }
                if (drainOutcome is null || drainOutcome.Disposition == SelfRelaunchDrainDisposition.ContinueTick)
                {
                    continue;
                }
                if (drainOutcome.Disposition == SelfRelaunchDrainDisposition.BreakLoop)
                {
                    break;
                }
            }
            RunJanitorialPhase("readmit-resolved-set-aside-goals", nextTick, () =>
            {
                ReadmitResolvedSetAsideGoals(
                    kernel, driver, sweepResult, onlyGoalId, setAsideGoals, selfClearedSetAsideEntries,
                    excludedGoals, escalatedGoals, reapedGoals, goalProjectionCache, _utcNow(), readmittedRetryReservations);
                ReadmitRecoveredSetAsideGoals(kernel, onlyGoalId, setAsideGoals, completedGoals,
                    escalatedGoals, dependencyEscalatedGoals, reapedGoals, advanceFaultRetries, goalProjectionCache);
                return true;
            });
            RunJanitorialPhase("mark-completed-dependency-goals", nextTick, () =>
            {
                MarkCompletedDependencyGoals(kernel, driver, onlyGoalId, completedGoals, goalProjectionCache);
                return true;
            });
            RunJanitorialPhase("reconcile-unscoped-dispatchable-goals", nextTick, () =>
            {
                ReconcileUnscopedDispatchableGoals(
                    kernel, driver, onlyGoalId, setAsideGoals, excludedGoals, escalatedGoals, reapedGoals,
                    completedGoals, unscopedDispatchableTicks, goalProjectionCache, unscopedStallTickThreshold, nextTick);
                return true;
            });
            sweepClock.Stop();
            var dependencyMetadataTiming = TerminalGoalJournalMetadataCache.CompleteMeasurement();
            preTickTimingLines.Add(FormatPhaseTiming(nextTick, "sweep", sweepClock.Elapsed,
                $"goals={kernel.Goals.Count} completed_dependencies={completedGoals.Count} set_aside={setAsideGoals.Count} dependency_metadata_ms={dependencyMetadataTiming.ElapsedMilliseconds} dependency_journals_read={dependencyMetadataTiming.JournalsRead}{FormatSweepCacheDetail(sweepResult)}{FormatSweepPhaseAttribution(_tickPhaseElapsedMs)}", cpuMs: EndCpuPhase(sweepCpuStart, ref _tickCpuSweepMs)));

            _promptRolloutWatch?.EvaluateTick(kernel);
            RunJanitorialPhase("main-suspect-release", nextTick,
                () => ServiceMainSuspectRelease(driver, canaryTasks, canaryTasksGate));
            var preWalkClock = StartDiagnosticTimer();
            var preWalkCpuStart = ReadProcessCpu();
            RunJanitorialPhase("retire-until-goal-lessons", nextTick, () => ConductorTickStepLedger.Measure("retire-until-goal-lessons", () => RetireUntilGoalLessons(kernel)));
            var hostedChangedGoalIds = ServiceStewardAndAuthor(kernel, onlyGoalId);
            var actionableIntentGoalIds = new HashSet<string>(StringComparer.Ordinal);
            var preWalkIntentLines = ConductorTickStepLedger.Measure("workspace-intents", () => ServiceWorkspaceIntents(kernel));
            var preWalkIntentProcessed = preWalkIntentLines.Count > 0;
            var intentsAwaitingReload = 0;
            if (_operatorIntents is not null)
            {
                try
                {
                    actionableIntentGoalIds.UnionWith(ConductorTickStepLedger.Measure("operator-intent-list", () => _operatorIntents.ListActionableGoalIds()));
                }
                catch (Exception ex)
                {
                    EmitProgress(
                        $"OPERATOR_INTENT result=store-unavailable phase=list reason={SanitizeReason(ex.Message)}");
                }
            }

            _slotContentionHolds.Retain(kernel.Goals.Select(goal => goal.Id));
            var scopedGoals = kernel.Goals
                .Where(g => (onlyGoalId is null || g.Id.Value == onlyGoalId)
                    && !sweepTerminalizedGoalIds.Contains(g.Id)
                    && !checkpointHeldGoals.ContainsKey(g.Id.Value)
                    && (!ConductorOwnerQuestionHolds.ExcludesFromWalk(g.CurrentHold?.State) || actionableIntentGoalIds.Contains(g.Id.Value))
                    && (!excludedGoals.Contains(g.Id.Value) || actionableIntentGoalIds.Contains(g.Id.Value))
                    && (!setAsideGoals.ContainsKey(g.Id.Value) || actionableIntentGoalIds.Contains(g.Id.Value)))
                .ToArray();
            var verifiedGoalIdsAtTickStart = scopedGoals.Where(goal => goal.Status == GoalStatus.Verified)
                .Select(goal => goal.Id).ToHashSet();
            var scopedGoalsById = scopedGoals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
            var preWalkIntentChangedGoalIds = new HashSet<GoalId>(hostedChangedGoalIds);
            if (_operatorIntents is not null)
            {
                foreach (var actionableGoalId in actionableIntentGoalIds)
                {
                    if (!scopedGoalsById.TryGetValue(actionableGoalId, out var scopedGoal))
                    {
                        if (checkpointHeldGoals.ContainsKey(actionableGoalId))
                        {
                            EmitProgress(
                                $"OPERATOR_INTENT goal={ShortGoalId(actionableGoalId)} result=deferred reason=checkpoint-held");
                            continue;
                        }

                        var disposition = UnloadedGoalIntentDisposition.Decide(actionableGoalId, onlyGoalId, _goalReloadObservation);
                        if (disposition is UnloadedGoalIntentDisposition.AwaitingReload)
                        {
                            intentsAwaitingReload++;
                            EmitProgress($"OPERATOR_INTENT goal={ShortGoalId(actionableGoalId)} result=deferred reason=awaiting-goal-reload");
                            continue;
                        }
                        var rejection = (UnloadedGoalIntentDisposition.Rejected)disposition;
                        try
                        {
                            var rejectedLines = _operatorIntents.RejectPending(actionableGoalId, rejection.Reason, rejection.ReasonCode);
                            preWalkIntentLines.AddRange(rejectedLines);
                            preWalkIntentProcessed |= rejectedLines.Count > 0;
                        }
                        catch (Exception ex)
                        {
                            EmitProgress(
                                $"OPERATOR_INTENT goal={ShortGoalId(actionableGoalId)} result=store-unavailable phase=reject reason={SanitizeReason(ex.Message)}");
                        }

                        continue;
                    }

                    OperatorIntentExecutionResult intentResult;
                    OperatorIntentGoalApplicationResult application;
                    try
                    {
                        application = OperatorIntentGoalApplication.ApplyPending(
                            kernel, scopedGoal, _operatorIntents, driver.ParallelAcceptanceAttemptCoordinator);
                        intentResult = application.Result;
                    }
                    catch (Exception ex)
                    {
                        EmitProgress(
                            $"OPERATOR_INTENT goal={ShortGoalId(scopedGoal.Id.Value)} result=store-unavailable phase=execute reason={SanitizeReason(ex.Message)}");
                        continue;
                    }

                    preWalkIntentLines.AddRange(application.Lines);
                    preWalkIntentProcessed |= intentResult.ProgressLines.Count > 0;
                    if (intentResult.RejectedAdjudication)
                        preWalkIntentChangedGoalIds.Add(scopedGoal.Id);
                    if (intentResult.MutatedGoalState)
                    {
                        preWalkIntentChangedGoalIds.Add(scopedGoal.Id);
                        excludedGoals.Remove(scopedGoal.Id.Value);
                        setAsideGoals.Remove(scopedGoal.Id.Value);
                        selfClearedSetAsideEntries.Remove(scopedGoal.Id.Value);
                        escalatedGoals.Remove(scopedGoal.Id.Value);
                        completedGoals.Remove(scopedGoal.Id.Value);
                        reapedGoals.Remove(scopedGoal.Id.Value);
                        goalProjectionCache.Invalidate(scopedGoal.Id);
                    }
                }
            }

            var parkedExcludedCount = scopedGoals.Count(g => g.Status == GoalStatus.Parked);
            var terminalExcludedCount = scopedGoals.Count(IsPreWalkExcludedTerminalGoal);
            var preWalkCandidates = scopedGoals
                .Where(g => !IsPreWalkExcludedGoal(g))
                .ToArray();
            var eligible = preWalkCandidates
                .Where(g => !preWalkIntentChangedGoalIds.Contains(g.Id))
                .Where(g => ConductorTickStepLedger.Measure("eligibility", () => IsLoopEligibleGoal(g, driver, goalProjectionCache)))
                .ToArray();
            ResetScopedGoalStallCounters(eligible, unscopedDispatchableTicks);
            preWalkClock.Stop();
            AddLedgerPhaseTimings(preTickTimingLines, nextTick, "prewalk", preWalkClock.Elapsed,
                $"scoped={scopedGoals.Length} candidates={preWalkCandidates.Length} eligible={eligible.Length} deferred_intent={preWalkIntentChangedGoalIds.Count} excluded_parked={parkedExcludedCount} excluded_terminal={terminalExcludedCount} cache_entries={goalProjectionCache.Count}", cpuMs: EndCpuPhase(preWalkCpuStart, ref _tickCpuPrewalkMs));

            if (eligible.Length == 0)
            {
                if (preWalkIntentProcessed || preWalkIntentChangedGoalIds.Count > 0)
                {
                    totalTicks++;
                    var intentTickLines = new List<string>();
                    foreach (var line in preTickTimingLines.Concat(preWalkIntentLines))
                    {
                        EmitProgress(line, intentTickLines);
                    }

                    EmitProgress(
                        $"TICK_END tick={totalTicks} advanced=0 held={preWalkIntentChangedGoalIds.Count} escalated=0 done=0",
                        intentTickLines);
                    var intentStatePersisted = preWalkIntentChangedGoalIds.Count == 0;
                    if (preWalkIntentChangedGoalIds.Count > 0 && checkpointGoalTick is not null)
                    {
                        var requested = preWalkIntentChangedGoalIds.ToArray();
                        var durable = ApplyCheckpointOutcomes(
                            checkpointGoalTick(kernel, requested),
                            requested,
                            checkpointHeldGoals,
                            totalTicks,
                            "operator-intent",
                            intentTickLines);
                        intentStatePersisted = durable.Count == requested.Length;
                        if (durable.Count > 0)
                            CompletePersistedOperatorIntents(durable, intentTickLines);
                    }
                    else if (preWalkIntentChangedGoalIds.Count > 0 && persistGoalTick is not null)
                    {
                        PersistGoalTickOrThrow(
                            persistGoalTick,
                            kernel,
                            preWalkIntentChangedGoalIds.ToArray(),
                            totalTicks,
                            intentTickLines,
                            busyWriteDelay);
                        intentStatePersisted = true;
                    }
                    else if (preWalkIntentChangedGoalIds.Count > 0)
                    {
                        intentStatePersisted = TryPersistTick(
                            persistTick,
                            kernel,
                            totalTicks,
                            ResolveGoalContext(preWalkIntentChangedGoalIds, onlyGoalId),
                            "operator-intent",
                            intentTickLines,
                            busyWriteDelay);
                    }

                    if (checkpointGoalTick is null && intentStatePersisted && preWalkIntentChangedGoalIds.Count > 0)
                    {
                        CompletePersistedOperatorIntents(preWalkIntentChangedGoalIds, intentTickLines);
                    }

                    totalHeld += preWalkIntentChangedGoalIds.Count;
                    onTick?.Invoke(new BatchTickSummary(
                        totalTicks,
                        Advanced: 0,
                        Held: preWalkIntentChangedGoalIds.Count,
                        Escalated: 0,
                        Retried: 0,
                        Done: 0,
                        WatchSleeping: false)
                    {
                        ProgressLines = intentTickLines,
                        OperatorDispositions = buildOperatorDispositions?.Invoke(kernel) ?? []
                    });
                    if (IsStopRequested(stopFilePath))
                    {
                        stopRequested = true;
                        StopLoop("stop-after-operator-intent");
                        DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                        PersistGracefulDetachCheckpoint(
                            persistTick,
                            persistGoalTick,
                            kernel,
                            totalTicks,
                            onlyGoalId,
                            "stop-after-operator-intent",
                            null,
                            busyWriteDelay,
                            checkpointGoalTick,
                            checkpointHeldGoals);
                        break;
                    }

                    CompleteActivationTick(totalTicks);
                    if (!(keepAliveWhenIdle && watchInterval is not null))
                    {
                        continue;
                    }
                }

                // Daemon keep-alive polls an empty backlog. Independently, any set-aside non-terminal goal
                // must reach another sweep/recheck even for a non-daemon invocation; otherwise a transient
                // escalation is indistinguishable from terminal completion and only a manual relaunch can
                // recover it.
                var recheckableBlockedGoals = CountRecheckableNonTerminalGoals(
                    kernel,
                    onlyGoalId,
                    setAsideGoals,
                    transientRecheckableGoalIds: checkpointHeldGoals.Keys.ToHashSet(StringComparer.Ordinal));
                var transientLoadRecheckPending = hasTransientLoadHold?.Invoke() == true;
                if ((keepAliveWhenIdle && watchInterval is not null) || recheckableBlockedGoals > 0 || transientLoadRecheckPending || intentsAwaitingReload > 0)
                {
                    if (recheckableBlockedGoals > 0 || transientLoadRecheckPending || intentsAwaitingReload > 0)
                    {
                        blockedRecheckCycles++;
                        totalBlockedRechecks++;
                        UpdateBlockedRecheckRecurrences(sweepResult, setAsideGoals, blockedRecheckRecurrences);
                        var now = _utcNow();
                        if (lastBlockedRecheckHeartbeatAt is null ||
                            now - lastBlockedRecheckHeartbeatAt.Value >= _blockedRecheckHeartbeatInterval)
                        {
                            EmitProgress(FormatBlockedRecheckHeartbeat(blockedRecheckRecurrences, totalBlockedRechecks));
                            lastBlockedRecheckHeartbeatAt = now;
                        }
                        var blockedRecheckBudget = maxIterations ??
                            (watchInterval is not null
                                ? null
                                : DefaultBlockedRecheckCycles);
                        var blockedRecheckBudgetUsed = maxIterations.HasValue
                            ? totalTicks + blockedRecheckCycles
                            : blockedRecheckCycles;
                        if (blockedRecheckBudget.HasValue &&
                            blockedRecheckBudgetUsed >= blockedRecheckBudget.Value &&
                            unscopedDispatchableTicks.Count == 0)
                        {
                            var exhaustedExplicitIterationBudget = maxIterations.HasValue;
                            var exhaustedStopReason = exhaustedExplicitIterationBudget
                                ? "max-iter"
                                : "blocked-recheck-exhausted";
                            StopLoop(exhaustedStopReason,
                                $"max={blockedRecheckBudget.Value} blockedRechecks={blockedRecheckCycles}");
                            Console.WriteLine(exhaustedExplicitIterationBudget
                                ? $"[conduct --loop] Max iterations ({blockedRecheckBudget.Value}) reached after {totalTicks} ticks and {blockedRecheckCycles} blocked rechecks."
                                : $"[conduct --loop] Blocked recheck budget ({blockedRecheckBudget.Value}) exhausted after {totalTicks} ticks and {blockedRecheckCycles} blocked rechecks.");
                            DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                            PersistGracefulDetachCheckpoint(
                                persistTick,
                                persistGoalTick,
                                kernel,
                                totalTicks,
                                onlyGoalId,
                                exhaustedExplicitIterationBudget ? "max-iterations" : "blocked-recheck-exhausted",
                                null,
                                busyWriteDelay,
                                checkpointGoalTick,
                                checkpointHeldGoals);
                            break;
                        }
                    }

                    var configuredInterval = watchInterval ?? TimeSpan.FromSeconds(DefaultWatchIntervalSeconds);
                    var idleInterval = ConsumeWatchInterval(
                        configuredInterval,
                        Math.Max(recheckableBlockedGoals, transientLoadRecheckPending ? 1 : 0));
                    if (totalTicks < nextTick)
                        CompleteActivationTick(nextTick);
                    EmitProgress(
                        recheckableBlockedGoals > 0 || transientLoadRecheckPending
                            ? $"BLOCKED_RECHECK_SLEEP goals={recheckableBlockedGoals}" +
                              (transientLoadRecheckPending ? " loadHeld=true" : string.Empty) +
                              $" seconds={(int)idleInterval.TotalSeconds}"
                            : $"IDLE_SLEEP seconds={(int)idleInterval.TotalSeconds}");
                    var idleSleep = sleepFunc is not null
                        ? (sleepFunc(idleInterval) ? WatchSleepResult.StopRequested : WatchSleepResult.FallbackElapsed)
                        : SleepUntilNextTick(idleInterval, stopFilePath, wakeSignal, GetRunningDispatchExitCodePaths(kernel, onlyGoalId), GetRunningAttemptExitCodePaths(driver, kernel, onlyGoalId));
                    if (idleSleep == WatchSleepResult.WakeSignaled)
                    {
                        RunJanitorialPhase(
                            "idle-wake-sweep",
                            nextTick,
                            () => _sweep(kernel, checkpointHeldGoals.Keys.ToHashSet(StringComparer.Ordinal)));
                        RunJanitorialPhase("idle-wake-recover-interrupted-dispatches", nextTick, () =>
                        {
                            _recoverInterruptedDispatches(kernel);
                            return true;
                        });
                        TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "idle-wake-sweep", null, busyWriteDelay,
                            checkpointGoalTick: checkpointGoalTick, checkpointHeldGoalIds: checkpointHeldGoals);
                    }

                    if (idleSleep == WatchSleepResult.StopRequested || IsStopRequested(stopFilePath))
                    {
                        stopRequested = true;
                        StopLoop("stop-while-idle");
                        DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                        PersistGracefulDetachCheckpoint(
                            persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop-while-idle", null, busyWriteDelay,
                            checkpointGoalTick, checkpointHeldGoals);
                        break;
                    }

                    continue;
                }

                var remainingNonTerminalGoals = kernel.Goals.Count(goal =>
                    (onlyGoalId is null || goal.Id.Value == onlyGoalId) &&
                    !IsTerminalGoal(goal));
                foreach (var line in preTickTimingLines)
                {
                    EmitProgress(line);
                }
                StopLoop(
                    remainingNonTerminalGoals == 0 ? "all-terminal" : "no-recheckable-work",
                    remainingNonTerminalGoals == 0 ? null : $"nonTerminalGoals={remainingNonTerminalGoals}");
                Console.WriteLine($"[conduct --loop] All goals done or escalated; loop complete after {totalTicks} ticks.");
                break;
            }

            blockedRecheckCycles = 0;
            totalTicks++;
            var tickLines = new List<string>();
            foreach (var line in preTickTimingLines)
            {
                EmitProgress(line, tickLines);
            }
            foreach (var line in preWalkIntentLines)
            {
                EmitProgress(line, tickLines);
            }

            var changedGoalLines = new List<string>();
            var changedGoalIds = new HashSet<GoalId>(preWalkIntentChangedGoalIds.Concat(GoalKernelChange.ChangedSince(kernel, sweepResult?.ReloadBaseline ?? preSweepBaseline, checkpointHeldGoals.Keys, sweepTerminalizedGoalIds)));
            var liveChangeSnapshots = new Dictionary<(string Worktree, string? BaseCommit), DispatchLiveChangeSnapshot>();

            DispatchLiveChangeSnapshot LiveChangesFor(TaskSpec task)
            {
                var dispatch = task.LastDispatch!;
                var key = (dispatch.WorkingDirectory, dispatch.BaseCommit);
                if (!liveChangeSnapshots.TryGetValue(key, out var snapshot))
                {
                    snapshot = GoalChangesReader.BuildLiveDispatchSnapshot(
                        dispatch.WorkingDirectory,
                        dispatch.BaseCommit,
                        displayLimit: 3);
                    liveChangeSnapshots[key] = snapshot;
                }

                return snapshot;
            }

            var tickAdvanced = 0;
            var tickHeld = 0;
            var tickEscalated = 0;
            var tickRetried = 0;
            var tickDone = 0;
            var dispatchRecordWriteSkippedGoals = new HashSet<string>(StringComparer.Ordinal);
            var dispatchRecordWriteBoundGoalIds = new HashSet<GoalId>();
            var parallelLandingResults = RunParallelAcceptanceBatch(
                eligible,
                scopedGoals,
                verifiedGoalIdsAtTickStart,
                preWalkIntentChangedGoalIds,
                kernel,
                driver,
                policy,
                completedGoals,
                escalatedGoals,
                totalTicks,
                changedGoalLines,
                changedGoalIds,
                suppressNewAcceptanceAdmission: isDeferringMaxDurationStop);

            var previousPhaseTimingSink = driver.PhaseTimingSink;
            var perGoalPhaseTimingLines = new List<string>();
            driver.PhaseTimingSink = line => perGoalPhaseTimingLines.Add($"PHASE_TIMING tick={totalTicks} {line}");
            var goalWalkTimings = new List<GoalWalkTiming>();
            driver.BeginTick(kernel, totalTicks);
            var goalWalkClock = StartDiagnosticTimer();
            var goalWalkCpuStart = ReadProcessCpu();
            var glanceDurationStats = _progressiveReviewGlances is null
                ? Array.Empty<TaskDurationStatsRecord>()
                : kernel.BuildTaskDurationStats();
            foreach (var goal in eligible)
            {
                if (isDeferringMaxDurationStop &&
                    !parallelLandingResults.ContainsKey(goal.Id.Value))
                {
                    _refreshGoalDispatchesBeforeAdvance(kernel, goal);
                    continue;
                }
                if (pendingSelfRelaunch is not null &&
                    !parallelLandingResults.ContainsKey(goal.Id.Value))
                {
                    break;
                }

                var label = goal.Id.Value[..8];
                var singleGoalClock = StartDiagnosticTimer();
                void FinishGoalWalk(string result)
                {
                    if (!singleGoalClock.IsRunning)
                    {
                        return;
                    }

                    singleGoalClock.Stop();
                    goalWalkTimings.Add(new GoalWalkTiming(label, result, singleGoalClock.Elapsed));
                }

                if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "walk-start", tickLines, FinishGoalWalk)) continue;
                // Dependency ordering is a dispatch-start gate. Do not interrupt a worker that is
                // currently in flight, but re-evaluate the edge before any later dispatch starts.
                var dependencyRequiresPerson = true;
                var depHoldReason = HasStartedGoalWork(goal)
                    ? null
                    : GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel, out dependencyRequiresPerson, dependencyEscalatedGoals);
                if (depHoldReason is not null)
                {
                    var progressLine = $"GOAL goal={label} result=held reason={SanitizeReason(depHoldReason)}";
                    if (RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → held: {depHoldReason}");
                        kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: held: {depHoldReason}");
                    }
                    // A goal held due to a failed/escalated dependency will never unblock unless
                    // future condition-specific re-entry logic says otherwise.
                    if (depHoldReason.StartsWith("dependency escalated", StringComparison.Ordinal) ||
                        depHoldReason.StartsWith("dependency-terminal-without-landing", StringComparison.Ordinal))
                    {
                        ClearGoalHold(kernel, goal, changedGoalIds);
                        escalatedGoals.Add(goal.Id.Value);
                        ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines);
                        SetAsideDependencyEscalated(kernel, driver, goal, depHoldReason, setAsideGoals, selfClearedSetAsideEntries, dependencyEscalatedGoals);
                        tickEscalated++;
                    }
                    else
                    {
                        if (dependencyRequiresPerson)
                        {
                            TrackGoalHold(
                                kernel,
                                goal,
                                TryResolveLifecycleState(goalProjectionCache, driver, goal),
                                depHoldReason,
                                _utcNow(),
                                effectiveGoalStallThreshold,
                                changedGoalIds,
                                tickLines, driver: driver);
                        }
                        else
                        {
                            ClearGoalHold(kernel, goal, changedGoalIds);
                        }
                        tickHeld++;
                    }

                    FinishGoalWalk("dependency-held");
                    continue;
                }

                if (driver.ParallelAcceptanceAttemptCoordinator.TryGetLiveInvalidatedAttempt(
                        goal.Id.Value,
                        out var invalidatedAttempt))
                {
                    var holdReason =
                        $"invalidated acceptance attempt {invalidatedAttempt.AttemptId} process {invalidatedAttempt.OwnerProcessId} is still exiting";
                    var progressLine = $"GOAL goal={label} result=held reason={SanitizeReason(holdReason)}";
                    if (RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → held: {holdReason}");
                        kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: held: {holdReason}");
                    }

                    tickHeld++;
                    TrackGoalHold(
                        kernel,
                        goal,
                        TryResolveLifecycleState(goalProjectionCache, driver, goal),
                        holdReason,
                        _utcNow(),
                        effectiveGoalStallThreshold,
                        changedGoalIds,
                        tickLines, driver: driver);
                    FinishGoalWalk("acceptance-cancellation-pending");
                    continue;
                }

                if (VerifiedAcceptanceEscalationDecision.HasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver))
                {
                    var progressLine = $"GOAL goal={label} result=escalated state={GoalLifecycleState.Verified}";
                    if (RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated at Verified — acceptance verification already requires operator action.");
                    }

                    escalatedGoals.Add(goal.Id.Value);
                    ClearGoalHold(kernel, goal, changedGoalIds);
                    ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines);
                    SetAside(kernel, driver, goal, BatchSetAsideCondition.LifecycleEscalation, setAsideGoals, selfClearedSetAsideEntries, sweepResult);
                    tickEscalated++;
                    FinishGoalWalk("verified-escalation");
                    continue;
                }

                var beforeAdvance = GoalKernelChange.Capture(goal);
                ConductorAdvanceResult result;
                ParallelLandingOutcome? parallelLandingOutcome = null;
                if (parallelLandingResults.TryGetValue(goal.Id.Value, out parallelLandingOutcome))
                {
                    result = parallelLandingOutcome.Result;
                }
                else
                {
                    if (!TryAdvanceGoal(
                        () =>
                        {
                            var beforeRefresh = GoalProjectionCache.BuildFingerprint(goal);
                            var runningHoldReason = DetachedDispatchHoldReasonBuilder.Build(goal, _refreshGoalDispatchesBeforeAdvance(kernel, goal));
                            if (_progressiveReviewGlances is not null && watchInterval is not null)
                            {
                                var glanceResult = _progressiveReviewGlances.Observe(
                                    kernel,
                                    [goal],
                                    glanceDurationStats,
                                    LiveChangesFor);
                                foreach (var line in glanceResult.ProgressLines)
                                {
                                    EmitProgress(line, tickLines);
                                }

                                if (glanceResult.MutatedTaskState)
                                {
                                    changedGoalIds.Add(goal.Id);
                                }
                            }

                            if (_progressiveReviewSteering is not null && watchInterval is not null)
                            {
                                var steerResult = _progressiveReviewSteering.ExecutePending(kernel, goal, policy);
                                foreach (var line in steerResult.ProgressLines)
                                {
                                    EmitProgress(line, tickLines);
                                }

                                if (steerResult.MutatedTaskState)
                                {
                                    changedGoalIds.Add(goal.Id);
                                    kernel.ClearGoalHold(goal.Id);
                                    tickHeld++;
                                    goalProjectionCache.Invalidate(goal.Id);
                                    FinishGoalWalk("progressive-review-steer");
                                    return null;
                                }
                            }

                            goalProjectionCache.Invalidate(goal.Id);
                            var afterRefresh = GoalProjectionCache.BuildFingerprint(goal);
                            if (!string.Equals(beforeRefresh, afterRefresh, StringComparison.Ordinal))
                            {
                                changedGoalIds.Add(goal.Id);
                            }

                            var engineHealth = _acceptanceEngineCircuit?.Read();
                            return IsAcceptanceEngineCircuitHoldRequired(goal.Status, engineHealth)
                                ? ParallelAcceptanceHeld(
                                    goal,
                                    policy,
                                    BuildAcceptanceEngineHoldReason(engineHealth!))
                                : driver.AdvanceOnce(goal, policy, runningHoldReason);
                        },
                        kernel,
                        driver,
                        goal,
                        policy,
                        totalTicks,
                        label,
                        changedGoalLines,
                        lastGoalDisposition,
                        changedGoalIds,
                        escalatedGoals,
                        reapedGoals,
                        setAsideGoals,
                        selfClearedSetAsideEntries,
                        dispatchRecordWriteSkips,
                        dispatchRecordWriteSkippedGoals,
                        dispatchRecordWriteBoundGoalIds,
                        tickLines,
                        ref tickHeld,
                        ref tickEscalated,
                        FinishGoalWalk,
                        out result))
                    {
                        if (GoalKernelChange.Changed(beforeAdvance, kernel, goal.Id)) changedGoalIds.Add(goal.Id);
                        continue;
                    }
                }

                // Auto-retry transient acceptance verification failures (up to maxVerifyRetries re-verifications)
                var serialRetryRan = false;
                if (!isDeferringMaxDurationStop && result.WasEscalated && VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(result))
                {
                    retryCounts.TryGetValue(goal.Id.Value, out var retries);
                    var retryAdvanceFaulted = false;
                    while (retries < maxVerifyRetries && result.WasEscalated && VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(result))
                    {
                        var nextRetry = retries + 1;
                        if (!TryAdvanceGoal(
                            () =>
                            {
                                var retryResult = driver.AdvanceOnce(goal, policy);
                                serialRetryRan = true;
                                retries = nextRetry;
                                retryCounts[goal.Id.Value] = retries;
                                tickRetried++;
                                changedGoalLines.Add($"GOAL goal={label} result=retry attempt={retries}/{maxVerifyRetries}");
                                lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
                                changedGoalIds.Add(goal.Id);
                                Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {goal.Id.Value[..8]} acceptance flake (retry {retries}/{maxVerifyRetries})");
                                kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop auto-retry acceptance verification (attempt {retries}/{maxVerifyRetries})");
                                goalProjectionCache.Invalidate(goal.Id);
                                return retryResult;
                            },
                            kernel,
                            driver,
                            goal,
                            policy,
                            totalTicks,
                            label,
                            changedGoalLines,
                            lastGoalDisposition,
                            changedGoalIds,
                            escalatedGoals,
                            reapedGoals,
                            setAsideGoals,
                            selfClearedSetAsideEntries,
                            dispatchRecordWriteSkips,
                            dispatchRecordWriteSkippedGoals,
                            dispatchRecordWriteBoundGoalIds,
                            tickLines,
                            ref tickHeld,
                            ref tickEscalated,
                            FinishGoalWalk,
                            out result))
                        {
                            if (GoalKernelChange.Changed(beforeAdvance, kernel, goal.Id)) changedGoalIds.Add(goal.Id);
                            retryAdvanceFaulted = true;
                            break;
                        }
                    }

                    if (retryAdvanceFaulted)
                        continue;
                }

                if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "post-advance", tickLines, FinishGoalWalk)) continue;

                if (GoalKernelChange.Changed(beforeAdvance, kernel, goal.Id)) changedGoalIds.Add(goal.Id);
                if (TryReconcileAwaitingVerificationHold(kernel, goal, result, totalTicks, out var reconciledOutcome))
                {
                    changedGoalIds.Add(goal.Id);
                    result = result with { Outcome = reconciledOutcome };
                    goalProjectionCache.Invalidate(goal.Id);
                }

                TrackGoalOutcomeAndCapacity(
                    kernel,
                    driver,
                    goal,
                    result.Outcome,
                    _utcNow(),
                    effectiveGoalStallThreshold,
                    changedGoalIds,
                    tickLines);

                var goalProgressLine = FormatGoalProgressLine(
                    label,
                    result.Outcome,
                    serialRetryRan ? null : parallelLandingOutcome?.SlotIndex);
                if (RecordChangedDisposition(
                    goal.Id.Value,
                    goalProgressLine,
                    lastGoalDisposition,
                    changedGoalLines,
                    ShouldAlwaysEmitDisposition(result.Outcome)))
                {
                    // Changed held advances are already included by the before/after comparison.
                    if (!result.IsHeld)
                        changedGoalIds.Add(goal.Id);
                    Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → {FormatOutcome(result.Outcome)}");
                    kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {FormatOutcome(result.Outcome)}",
                        VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(result.Outcome));
                }

                if (result.WasExecuted)        { tickAdvanced++; }
                else if (result.IsHeld)        { tickHeld++; }
                else if (result.WasEscalated)  { tickEscalated++; escalatedGoals.Add(goal.Id.Value); ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines); SetAside(kernel, driver, goal, GetSetAsideCondition(result), setAsideGoals, selfClearedSetAsideEntries, sweepResult); }
                else if (result.IsDone)        { tickDone++;      completedGoals.Add(goal.Id.Value); excludedGoals.Add(goal.Id.Value); }
                goalProjectionCache.Invalidate(goal.Id);
                FinishGoalWalk(result.Outcome.GetType().Name);
            }
            goalWalkClock.Stop();
            ObserveWorkerCapacityTick(kernel, effectiveGoalStallThreshold);
            ConductorExperimentWatch.For(this, _workspace, CurrentConductEventLogWriter.Value ?? _conductEventLogWriter)?.ObserveTick(_utcNow());
            driver.PhaseTimingSink = previousPhaseTimingSink;
            foreach (var line in perGoalPhaseTimingLines)
            {
                EmitProgress(line, tickLines);
            }

            EmitProgress(FormatPhaseTiming(totalTicks, "per-goal-walk", goalWalkClock.Elapsed,
                $"goals={goalWalkTimings.Count} slowest={FormatSlowestGoalWalks(goalWalkTimings)}", cpuMs: EndCpuPhase(goalWalkCpuStart, ref _tickCpuWalkMs)), tickLines);

            totalAdvanced  += tickAdvanced;
            totalHeld      += tickHeld;
            totalEscalated += tickEscalated;
            totalRetried   += tickRetried;
            totalDone      += tickDone;

            var emitTickSummary = changedGoalLines.Count > 0
                || parkedExcludedCount > 0
                || totalTicks % QuietSummaryEveryTicks == 0;
            if (watchInterval is not null)
            {
                EmitWatchProgress(eligible, quiet, policy, watchInterval, stallWarningThreshold,
                    liveChangeSnapshots, totalTicks, tickLines);
            }

            if (emitTickSummary)
            {
                EmitProgress($"TICK tick={totalTicks} eligible={eligible.Length}", tickLines);
                foreach (var line in changedGoalLines)
                {
                    EmitProgress(line, tickLines);
                }

                if (parkedExcludedCount > 0)
                {
                    EmitProgress($"TICK_EXCLUDED tick={totalTicks} kind=parked count={parkedExcludedCount}", tickLines);
                }

                var summaryPrefix = changedGoalLines.Count > 0 ? "TICK_END" : "TICK_SUMMARY";
                EmitProgress($"{summaryPrefix} tick={totalTicks} advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} done={tickDone}{FormatTickCpuSummary()}", tickLines);
                Console.WriteLine($"[conduct --loop] Tick {totalTicks} summary: advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} retried={tickRetried} done={tickDone}");
            }

            CaptureLandingTickChanges(changedGoalIds, pendingSelfRelaunch is not null);
            // Durably checkpoint this tick's progress (dispatches started, reconcile results, escalations).
            // Without this the loop's mutations live only in memory until the whole command returns, so a
            // long-running watch loop never persists and a killed loop loses every dispatch on rollback —
            // the goal then re-dispatches the same stage forever and can never advance.
            // When persistGoalTick is supplied, persist only the goals whose disposition changed this tick
            // in one bulk checkpoint. The per-goal loop above is justified because it runs each goal's
            // state machine; the durable write is intentionally batched. A contention-bound escalation is
            // persisted separately so the same BUSY/LOCKED condition remains a per-goal degradation instead
            // of being promoted back into a loop-fatal end-of-tick write.
            if (checkpointGoalTick is not null)
            {
                var criticalGoalIds = changedGoalIds
                    .Except(dispatchRecordWriteBoundGoalIds)
                    .ToArray();
                if (criticalGoalIds.Length > 0)
                {
                    var durable = ApplyCheckpointOutcomes(
                        checkpointGoalTick(kernel, criticalGoalIds),
                        criticalGoalIds,
                        checkpointHeldGoals,
                        totalTicks,
                        "goal",
                        tickLines);
                    if (durable.Count > 0)
                        CompletePersistedOperatorIntents(durable, tickLines);
                }

                if (dispatchRecordWriteBoundGoalIds.Count > 0)
                {
                    var boundGoalIds = dispatchRecordWriteBoundGoalIds.ToArray();
                    var durable = ApplyCheckpointOutcomes(
                        checkpointGoalTick(kernel, boundGoalIds),
                        boundGoalIds,
                        checkpointHeldGoals,
                        totalTicks,
                        "dispatch-record-contention-escalation",
                        tickLines);
                    if (durable.Count > 0)
                        CompletePersistedOperatorIntents(durable, tickLines);
                }
            }
            else if (persistGoalTick is not null)
            {
                var criticalGoalIds = changedGoalIds
                    .Except(dispatchRecordWriteBoundGoalIds)
                    .ToArray();
                if (criticalGoalIds.Length > 0)
                {
                    PersistGoalTickOrThrow(persistGoalTick, kernel, criticalGoalIds, totalTicks, tickLines, busyWriteDelay);
                    CompletePersistedOperatorIntents(criticalGoalIds, tickLines);
                }

                if (dispatchRecordWriteBoundGoalIds.Count > 0)
                {
                    var boundGoalIds = dispatchRecordWriteBoundGoalIds.ToArray();
                    if (TryPersistGoalTick(
                            persistGoalTick,
                            kernel,
                            boundGoalIds,
                            totalTicks,
                            "dispatch-record-contention-escalation",
                            tickLines,
                            busyWriteDelay))
                    {
                        CompletePersistedOperatorIntents(boundGoalIds, tickLines);
                    }
                    else
                    {
                        EmitProgress(
                            $"DISPATCH_RECORD_ESCALATION_PERSIST_DEFERRED tick={totalTicks} goal={ResolveGoalContext(boundGoalIds, onlyGoalId: null)} reason=sqlite-busy-retry-exhausted",
                            tickLines);
                    }
                }
            }
            else
            {
                var tickPersisted = TryPersistTick(
                    persistTick,
                    kernel,
                    totalTicks,
                    ResolveGoalContext(changedGoalIds, onlyGoalId),
                    "tick",
                    tickLines,
                    busyWriteDelay);
                if (tickPersisted && changedGoalIds.Count > 0)
                {
                    CompletePersistedOperatorIntents(changedGoalIds, tickLines);
                }
            }
            SaveLandingTickBeforeRelaunch(
                pendingSelfRelaunch is not null, kernel, checkpointGoalTick, persistGoalTick, persistTick, checkpointHeldGoals, totalTicks, busyWriteDelay);

            var operatorDispositions = buildOperatorDispositions?.Invoke(kernel) ?? [];
            var tickSummary = new BatchTickSummary(totalTicks, tickAdvanced, tickHeld, tickEscalated, tickRetried, tickDone, WatchSleeping: false)
            {
                ProgressLines = tickLines,
                OperatorDispositions = operatorDispositions
            };
            if (tickAdvanced == 0 && tickDone == 0)
            {
                if (watchInterval is null)
                {
                    if (CountRecheckableNonTerminalGoals(
                            kernel,
                            onlyGoalId,
                            setAsideGoals,
                            transientRecheckableGoalIds: dispatchRecordWriteSkippedGoals.Concat(checkpointHeldGoals.Keys).ToHashSet(StringComparer.Ordinal)) > 0)
                    {
                        onTick?.Invoke(tickSummary);
                        CompleteActivationTick(totalTicks);
                        continue;
                    }

                    StopLoop("no-progress-no-watch");
                    Console.WriteLine($"[conduct --loop] No progress in tick {totalTicks}; all eligible goals held or escalated.");
                    DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "no-progress", tickLines, busyWriteDelay,
                        checkpointGoalTick, checkpointHeldGoals);
                    onTick?.Invoke(tickSummary);
                    break;
                }

                var recheckableBlockedGoals = CountRecheckableNonTerminalGoals(
                    kernel,
                    onlyGoalId,
                    setAsideGoals,
                        transientRecheckableGoalIds: dispatchRecordWriteSkippedGoals.Concat(checkpointHeldGoals.Keys).ToHashSet(StringComparer.Ordinal));
                var fallbackInterval = ConsumeWatchInterval(watchInterval.Value, recheckableBlockedGoals);
                var sleepSeconds = (int)fallbackInterval.TotalSeconds;
                if (CountRecheckableNonTerminalGoals(
                        kernel,
                        onlyGoalId,
                        setAsideGoals,
                        transientRecheckableGoalIds: dispatchRecordWriteSkippedGoals.Concat(checkpointHeldGoals.Keys).ToHashSet(StringComparer.Ordinal)) > 0)
                {
                    totalBlockedRechecks++;
                    UpdateBlockedRecheckRecurrences(sweepResult, setAsideGoals, blockedRecheckRecurrences);
                    var now = _utcNow();
                    if (lastBlockedRecheckHeartbeatAt is null ||
                        now - lastBlockedRecheckHeartbeatAt.Value >= _blockedRecheckHeartbeatInterval)
                    {
                        EmitProgress(FormatBlockedRecheckHeartbeat(blockedRecheckRecurrences, totalBlockedRechecks));
                        lastBlockedRecheckHeartbeatAt = now;
                    }
                }
                CompleteActivationTick(totalTicks);
                if (emitTickSummary)
                {
                    EmitProgress($"WATCH_SLEEP tick={totalTicks} seconds={sleepSeconds}");
                    Console.WriteLine($"[conduct --loop --watch] No progress in tick {totalTicks}; sleeping {sleepSeconds}s for workers to complete.");
                }
                onTick?.Invoke(tickSummary with { WatchSleeping = true });

                var sleepResult = sleepFunc is not null
                    ? (sleepFunc(fallbackInterval) ? WatchSleepResult.StopRequested : WatchSleepResult.FallbackElapsed)
                    : SleepUntilNextTick(fallbackInterval, stopFilePath, wakeSignal, GetRunningDispatchExitCodePaths(kernel, onlyGoalId), GetRunningAttemptExitCodePaths(driver, kernel, onlyGoalId));

                if (sleepResult == WatchSleepResult.WakeSignaled)
                {
                    EmitProgress(FormatWatchWake(totalTicks, stopFilePath, wakeSignal));
                    RunJanitorialPhase(
                        "wake-sweep",
                        totalTicks,
                        () => _sweep(kernel, checkpointHeldGoals.Keys.ToHashSet(StringComparer.Ordinal)));
                    RunJanitorialPhase("wake-recover-interrupted-dispatches", totalTicks, () =>
                    {
                        _recoverInterruptedDispatches(kernel);
                        return true;
                    });
                    TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "wake-sweep", tickLines, busyWriteDelay,
                        checkpointGoalTick: checkpointGoalTick, checkpointHeldGoalIds: checkpointHeldGoals);
                }

                if (sleepResult == WatchSleepResult.StopRequested || IsStopRequested(stopFilePath))
                {
                    stopRequested = true;
                    StopLoop("stop-file-during-sleep");
                    Console.WriteLine($"[conduct --loop --watch] Stop signal detected during sleep after tick {totalTicks}; no new dispatches.");
                    DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    PersistGracefulDetachCheckpoint(
                        persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop-during-sleep", tickLines, busyWriteDelay,
                        checkpointGoalTick, checkpointHeldGoals);
                    break;
                }

                continue;
            }

            onTick?.Invoke(tickSummary);
            CompleteActivationTick(totalTicks);
        }

        AwaitCanaryTasks(canaryTasks, canaryTasksGate);
        ConductorLoopHandoffResult? handoff = selfRelaunchHandoff;
        if (selfRelaunchHandoff is not null && stopReason is null)
        {
            StopLoop("self-relaunch-handoff");
        }
        if (maxDurationReached && _handoffOnMaxDuration is not null)
        {
            var landedGoalDelta = GetCompletedGoalIds(kernel).Except(initiallyCompletedGoalIds, StringComparer.Ordinal).Count();
            var request = new ConductorLoopHandoffRequest(totalTicks, maxDuration ?? TimeSpan.Zero, totalDone, landedGoalDelta);
            handoff = _handoffOnMaxDuration(request);
            EmitHandoffProgress(totalTicks, handoff);
        }

        if (stopReason is null)
        {
            StopLoop("loop-return");
        }
        return new BatchLoopSummary(totalTicks, totalAdvanced, totalHeld, totalEscalated, totalRetried, totalDone, stopRequested, handoff, stopReason, totalBlockedRechecks, GetSuccessfulLandingCount(landedGoalIds));
        }
        catch (Exception ex)
        {
            stopReason ??= "unintended-exit";
            var detail = $"exception={Sanitize(ex.GetType().Name)} message={SanitizeReason(ex.Message)}";
            var diagnosticLocation = RecordUnintendedExitDiagnostic(totalTicks, ex);
            try
            {
                lifecycleSession?.Stop("unintended-exit", totalTicks, detail);
            }
            catch (Exception diagnosticException)
            {
                TryWriteAbnormalExitDiagnosticFailure("lifecycle", diagnosticException);
            }

            try
            {
                EmitProgress(
                    $"LOOP_STOP tick={totalTicks} rechecks={totalBlockedRechecks} reason=unintended-exit {detail} diagnostic={diagnosticLocation}");
            }
            catch (Exception diagnosticException)
            {
                TryWriteAbnormalExitDiagnosticFailure("event", diagnosticException);
            }

            throw;
        }
        finally
        {
            StopStewardAndAuthor();
            DrainCanaryTasks(canaryTasks, canaryTasksGate);
            driver.SuccessfulLandingSink = previousSuccessfulLandingSink;
            driver.DispatchRecordWriteSucceededSink = previousDispatchRecordWriteSucceededSink;
            driver.LandingMutationBlocker = previousLandingMutationBlocker;
            foreach (var line in CurrentRetryDiagnostics.Value?.CompleteAll() ?? [])
                EmitProgress(line);
            CurrentRetryDiagnostics.Value = previousRetryDiagnostics;
            CurrentConductEventLogWriter.Value = previousConductEventLogWriter;
        }
    }

    private static void TryWriteAbnormalExitDiagnosticFailure(string sink, Exception exception)
    {
        try
        {
            Console.Error.WriteLine(
                $"[conduct --loop] Failed to record unintended exit in {sink}: " +
                $"{exception.GetType().Name}: {SanitizeReason(exception.Message)}");
        }
        catch
        {
        }
    }

    private bool TryAdvanceGoal(
        Func<ConductorAdvanceResult?> advance,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAutonomyPolicy policy,
        int totalTicks,
        string label,
        List<string> changedGoalLines,
        Dictionary<string, string> lastGoalDisposition,
        HashSet<GoalId> changedGoalIds,
        HashSet<string> escalatedGoals,
        HashSet<string> reapedGoals,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        Dictionary<string, BatchSetAsideEntry> selfClearedSetAsideEntries,
        Dictionary<string, int> dispatchRecordWriteSkips,
        HashSet<string> dispatchRecordWriteSkippedGoals,
        HashSet<GoalId> dispatchRecordWriteBoundGoalIds,
        List<string> tickLines,
        ref int tickHeld,
        ref int tickEscalated,
        Action<string> finishGoalWalk,
        out ConductorAdvanceResult result)
    {
        try
        {
            var advanceResult = advance();
            _slotContentionHolds.Clear(goal.Id);
            if (advanceResult is null)
            {
                result = null!;
                return false;
            }

            result = advanceResult;
            return true;
        }
        catch (DispatchRecordWriteException ex)
        {
            if (ex.IsFatal)
                throw;

            dispatchRecordWriteSkips.TryGetValue(goal.Id.Value, out var priorSkips);
            var skips = priorSkips + 1;
            dispatchRecordWriteSkips[goal.Id.Value] = skips;
            var code = ex.SqliteErrorCode?.ToString() ?? "unavailable";
            var (token, disposition) = ex.Cause == DispatchRecordWriteFailureCause.Contention
                ? ("DISPATCH_RECORD_WRITE_CONTENTION", "contention")
                : ("DISPATCH_RECORD_WRITE_UNCLASSIFIED", "unclassified");
            var diagnostic = ex.Cause is null
                ? $" exception={ex.InnerException?.GetType().FullName ?? "unavailable"} error={SanitizeReason(ex.InnerException?.Message ?? ex.Message)}"
                : string.Empty;
            EmitProgress(
                $"{token} tick={totalTicks} kind={ex.Kind} goal={label} task={ShortGoalId(ex.TaskId.Value)} sqliteCode={code} skip={skips}/{DispatchRecordContentionSkipLimit}{diagnostic}",
                tickLines);

            if (skips < DispatchRecordContentionSkipLimit)
            {
                dispatchRecordWriteSkippedGoals.Add(goal.Id.Value);
                tickHeld++;
                finishGoalWalk($"dispatch-record-{disposition}");
                result = null!;
                return false;
            }

            if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "advance-fault", tickLines, finishGoalWalk, out result)) return false;

            var reason = $"dispatch-record-write-{disposition}-limit count={skips}/{DispatchRecordContentionSkipLimit} sqliteCode={code}";
            changedGoalLines.Add($"GOAL goal={label} result=escalated reason={reason}");
            lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
            changedGoalIds.Add(goal.Id);
            dispatchRecordWriteBoundGoalIds.Add(goal.Id);
            kernel.ClearGoalHold(goal.Id);
            kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {reason}");
            escalatedGoals.Add(goal.Id.Value);
            ReapGoalOnce(kernel, kernel.GetGoal(goal.Id), reapedGoals, totalTicks, tickLines);
            SetAside(kernel, driver, kernel.GetGoal(goal.Id), BatchSetAsideCondition.AdvanceFault, setAsideGoals, selfClearedSetAsideEntries);
            tickEscalated++;
            finishGoalWalk($"dispatch-record-{disposition}-limit");
            result = null!;
            return false;
        }
        catch (Exception ex) when (ex is DotnetBuildSlotsBusyException or BuildLockBlockedException)
        {
            if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "build-slot-contention", tickLines, finishGoalWalk, out result))
            {
                _slotContentionHolds.Clear(goal.Id);
                return false;
            }

            var holds = _slotContentionHolds.RecordHold(goal.Id);
            if (holds == SlotContentionAttentionHoldLimit)
            {
                var detail = ex is DotnetBuildSlotsBusyException busy
                    ? FormatSlotsBusy(busy.SlotsBusy)
                    : FormatBuildLockBlocked(((BuildLockBlockedException)ex).Attribution);
                EmitProgress($"INFRASTRUCTURE_ATTENTION tick={totalTicks} kind=build-slot-contention goal={label} holds={holds} {detail}", tickLines);
            }

            tickHeld++;
            finishGoalWalk("build-slot-contention");
            result = null!;
            return false;
        }
        catch (Exception ex)
        {
            if (TrySkipGoalLeftWorkingSet(kernel, goal, totalTicks, "advance-fault", tickLines, finishGoalWalk, out result)) return false;

            var msg = $"Batch loop tick {totalTicks}: fault isolating goal — advance threw: {SanitizeReason(ex.Message)}";
            changedGoalLines.Add($"GOAL goal={label} result=escalated reason={SanitizeReason(ex.Message)}");
            lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
            changedGoalIds.Add(goal.Id);
            kernel.ClearGoalHold(goal.Id);
            Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated (advance threw): {ex.Message}");
            kernel.RecordGoalPolicyDecision(goal.Id, msg);
            escalatedGoals.Add(goal.Id.Value);
            ReapGoalOnce(kernel, goal, reapedGoals, totalTicks, tickLines);
            SetAsideAdvanceFault(kernel, driver, goal, SanitizeReason(ex.Message), setAsideGoals, selfClearedSetAsideEntries);
            tickEscalated++;
            finishGoalWalk("advance-fault");
            result = null!;
            return false;
        }
    }

    private static void AwaitCanaryTasks(
        List<Task<PostLandingCanaryDisposition>> tasks,
        object gate)
    {
        Task<PostLandingCanaryDisposition>[] snapshot;
        lock (gate)
        {
            snapshot = tasks.ToArray();
        }

        Task.WhenAll(snapshot).GetAwaiter().GetResult();
    }

    private static void DrainCanaryTasks(
        List<Task<PostLandingCanaryDisposition>> tasks,
        object gate)
    {
        try
        {
            AwaitCanaryTasks(tasks, gate);
        }
        catch
        {
            // Preserve the primary loop exception. AwaitCanaryTasks already observed every
            // worker and therefore still guarantees no canary process escapes this loop.
        }
    }

    private static void EmitHandoffProgress(
        int tick,
        ConductorLoopHandoffResult handoff,
        string? goalId = null)
    {
        var goal = goalId is null ? string.Empty : $" goal={goalId}";
        if (handoff.Started)
        {
            EmitProgress($"LOOP_HANDOFF tick={tick}{goal} pid={handoff.ProcessId} stdout={SanitizeHandoffDetail(handoff.StdoutPath ?? "")} stderr={SanitizeHandoffDetail(handoff.StderrPath ?? "")} verification={SanitizeHandoffDetail(handoff.VerificationOutcome ?? "unknown")}");
            Console.WriteLine($"[conduct --loop] Handoff started successor pid={handoff.ProcessId} log={handoff.StdoutPath}");
            return;
        }

        if (handoff.Failed)
        {
            EmitProgress($"LOOP_HANDOFF_FAILED tick={tick} reason={SanitizeHandoffDetail(handoff.Reason ?? "unknown")} stdout={SanitizeHandoffDetail(handoff.StdoutPath ?? "")} stderr={SanitizeHandoffDetail(handoff.StderrPath ?? "")} verification={SanitizeHandoffDetail(handoff.VerificationOutcome ?? "unknown")}");
            Console.WriteLine($"[conduct --loop] Handoff failed: {handoff.Reason ?? "unknown"}");
            return;
        }

        EmitProgress($"LOOP_HANDOFF_SKIPPED tick={tick} reason={SanitizeReason(handoff.Reason ?? "not-started")}");
        Console.WriteLine($"[conduct --loop] Handoff skipped: {handoff.Reason ?? "not-started"}");
    }

    private static void EmitSelfRelaunchRollback(
        int tick,
        string goalId,
        string phase,
        string reason) =>
        EmitProgress(
            $"LOOP_RELAUNCH_ROLLBACK tick={tick} goal={goalId} phase={SanitizeHandoffDetail(phase)} " +
            $"rolledBack=true continuing=true reason={SanitizeHandoffDetail(reason)}");

    private enum SelfRelaunchDrainDisposition
    {
        Proceed,
        ContinueTick,
        BreakLoop
    }

    private sealed record SelfRelaunchDrainOutcome(
        SelfRelaunchDrainDisposition Disposition,
        Exception? Fault = null);

    private T? RunJanitorialPhase<T>(
        string phase,
        int tick,
        Func<T> action)
    {
        var startTimestamp = _janitorialTimestamp();
        try
        {
            _janitorialPhaseProbe?.Invoke(phase);
            var result = action();

            if (_consecutiveJanitorialFailures.Remove(phase, out var skippedTicks))
            {
                EmitProgress(
                    $"LOOP_JANITORIAL_RECOVERED tick={tick} phase={SanitizeHandoffDetail(phase)} skippedTicks={skippedTicks}");
            }

            return result;
        }
        catch (SqliteException ex) when (SqliteOrchestratorStateRepository.IsTransientLock(ex))
        {
            RecordFailure(ex, transient: true);
            return default;
        }
        catch (Exception ex)
        {
            RecordFailure(ex, transient: false);
            return default;
        }
        finally
        {
            var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startTimestamp, _janitorialTimestamp()).TotalMilliseconds;
            _tickPhaseElapsedMs[phase] = _tickPhaseElapsedMs.GetValueOrDefault(phase) + elapsedMilliseconds;
        }

        void RecordFailure(Exception exception, bool transient)
        {
            var consecutiveFailures = _consecutiveJanitorialFailures.GetValueOrDefault(phase) + 1;
            _consecutiveJanitorialFailures[phase] = consecutiveFailures;
            EmitProgress(
                $"LOOP_JANITORIAL_FAILED tick={tick} phase={SanitizeHandoffDetail(phase)} transient={transient.ToString().ToLowerInvariant()} " +
                $"attempts=1 consecutiveFailures={consecutiveFailures} exception={exception.GetType().Name} " +
                $"message={SanitizeHandoffDetail(exception.Message)}");
            if (consecutiveFailures == JanitorialFailureEscalationThreshold)
            {
                EmitProgress(
                    $"LOOP_JANITORIAL_DEGRADED tick={tick} phase={SanitizeHandoffDetail(phase)} " +
                    $"consecutiveFailures={consecutiveFailures} workDeferred=true");
            }
        }
    }

    private static string ResolveGoalContext(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        if (onlyGoalId is not null)
        {
            return ShortGoalId(onlyGoalId);
        }

        var active = kernel.Goals
            .Where(goal => !IsTerminalGoal(goal))
            .Select(goal => ShortGoalId(goal.Id.Value))
            .Take(4)
            .ToArray();
        return active.Length == 0 ? "none" : string.Join(",", active);
    }

    private static string ResolveGoalContext(IReadOnlyCollection<GoalId> goalIds, string? onlyGoalId)
    {
        if (goalIds.Count > 0)
        {
            return string.Join(",", goalIds.Select(goalId => ShortGoalId(goalId.Value)).Take(4));
        }

        return onlyGoalId is null ? "none" : ShortGoalId(onlyGoalId);
    }

    private static string ShortGoalId(string goalId) =>
        goalId.Length <= 8 ? goalId : goalId[..8];

    private static bool RecordChangedDisposition(
        string goalId,
        string progressLine,
        Dictionary<string, string> lastGoalDisposition,
        List<string> changedGoalLines,
        bool alwaysRecord = false)
    {
        if (!alwaysRecord
            && lastGoalDisposition.TryGetValue(goalId, out var previous)
            && string.Equals(previous, progressLine, StringComparison.Ordinal))
        {
            return false;
        }

        lastGoalDisposition[goalId] = progressLine;
        changedGoalLines.Add(progressLine);
        return true;
    }

    private static bool ShouldAlwaysEmitDisposition(ConductorAdvanceOutcome outcome) =>
        outcome is ConductorAdvanceOutcome.Held { State: GoalLifecycleState.AwaitingVerification };

    private static bool TryReconcileAwaitingVerificationHold(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorAdvanceResult result,
        int tick,
        out ConductorAdvanceOutcome reconciledOutcome)
    {
        reconciledOutcome = result.Outcome;
        if (result.Outcome is not ConductorAdvanceOutcome.Held { State: GoalLifecycleState.AwaitingVerification })
        {
            return false;
        }

        var reason = $"Batch loop tick {tick}: reconciled all task verification gates; promoted goal to Verified.";
        if (!kernel.ReconcileGoalVerificationStatus(goal.Id, reason))
        {
            return false;
        }

        reconciledOutcome = new ConductorAdvanceOutcome.Executed(
            GoalLifecycleState.AwaitingVerification,
            "Reconciled all task verification gates; goal advanced to Verified");
        return true;
    }

    // Sanitize a detail string for compact line format (no spaces, max 40 chars).
    private static string Sanitize(string value)
    {
        var s = value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
        return s.Length > 40 ? s[..40] : s;
    }

    private static string SanitizeReceiptToken(string value) =>
        value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    internal static string SanitizeReason(string value)
    {
        const int maxReasonLength = 512;
        var sanitized = value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
        return sanitized.Length > maxReasonLength ? sanitized[..maxReasonLength] : sanitized;
    }

    private static string SanitizeHeartbeatDetail(string value)
    {
        const int maxDetailLength = 160;
        var sanitized = value
            .Replace(' ', '_')
            .Replace('\t', '_')
            .Replace('\n', '_')
            .Replace('\r', '_')
            .Replace(',', '_')
            .Replace('|', '_')
            .Replace('[', '_')
            .Replace(']', '_');
        return sanitized.Length > maxDetailLength ? sanitized[..maxDetailLength] : sanitized;
    }

    private static string SanitizeHandoffDetail(string value) =>
        value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    private static bool PoliciesMatch(ConductorAutonomyPolicy left, ConductorAutonomyPolicy right) =>
        string.Equals(left.ToJson(), right.ToJson(), StringComparison.Ordinal);

    private static string FormatPolicyValues(string prefix, ConductorAutonomyPolicy policy)
    {
        var transitions = string.Join(',', policy.TransitionMap
            .OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key}:{entry.Value}"));

        return $"{prefix}Name={Sanitize(policy.Name)} " +
               $"{prefix}MaxConcurrentPaidWorkers={policy.MaxConcurrentPaidWorkers} " +
               $"{prefix}MaxCriterionRetries={policy.MaxCriterionRetries} " +
               $"{prefix}AutoPromoteRiskThreshold={policy.AutoPromoteRiskThreshold?.ToString() ?? "none"} " +
               $"{prefix}MaxEmptyOutputDispatchRetries={policy.MaxEmptyOutputDispatchRetries} " +
               $"{prefix}MaxEmptyOutputAutoRecoverCycles={policy.MaxEmptyOutputAutoRecoverCycles} " +
               $"{prefix}EmptyOutputRetryInitialDelaySeconds={policy.EmptyOutputRetryInitialDelaySeconds} " +
               $"{prefix}EmptyOutputRetryBackoffMultiplier={policy.EmptyOutputRetryBackoffMultiplier} " +
               $"{prefix}EmptyOutputRetryMaxDelaySeconds={policy.EmptyOutputRetryMaxDelaySeconds} " +
               $"{prefix}ReviewAutoRetryWarningRound={policy.ReviewAutoRetryWarningRound} " +
               $"{prefix}ReviewAutoRetryStopRound={policy.ReviewAutoRetryStopRound} " +
               $"{prefix}ReviewAutoRetryLifetimeMultiplier={policy.ReviewAutoRetryLifetimeMultiplier} " +
               $"{prefix}TransitionMap={transitions}";
    }

    private IReadOnlyDictionary<string, ParallelLandingOutcome> RunParallelAcceptanceBatch(
        IReadOnlyList<Goal> eligible,
        IReadOnlyList<Goal> scopedGoals,
        IReadOnlySet<GoalId> verifiedGoalIdsAtTickStart,
        IReadOnlySet<GoalId> preWalkIntentChangedGoalIds,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds,
        bool suppressNewAcceptanceAdmission)
    {
        var configuredAcceptanceWidth = policy.AcceptanceWidth;
        if (configuredAcceptanceWidth < ConductorAutonomyPolicy.MinimumAcceptanceWidth)
        {
            return new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        }
        var state = BeginParallelAcceptanceBatch(
            eligible, kernel, driver, policy, completedGoals, escalatedGoals,
            configuredAcceptanceWidth, tick, changedGoalLines);
        if (state.CapacityStateUnavailable) return state.Results;

        ReconcileParallelAcceptanceAttempts(state, kernel, driver, policy, tick, changedGoalLines, changedGoalIds);
        PrepareGroupedAcceptanceAdmission(
            state, eligible, scopedGoals, verifiedGoalIdsAtTickStart, preWalkIntentChangedGoalIds,
            kernel, driver, policy, completedGoals, escalatedGoals, tick, changedGoalLines,
            suppressNewAcceptanceAdmission);
        AdmitMergeTrain(state, driver, policy, tick, changedGoalLines);
        AdmitPairCohort(state, kernel, driver, policy, tick, changedGoalLines, changedGoalIds,
            suppressNewAcceptanceAdmission);
        HoldCohortMembers(state, driver, policy, tick, changedGoalLines);
        if (suppressNewAcceptanceAdmission) return state.Results;
        HoldLoneReadyGoalForInReviewCohortPartner(kernel, scopedGoals, state.OrderedEligible, state.ProductionCandidates,
            state.AcceptanceCensus, driver, policy, state.Results, tick, changedGoalLines);
        AdmitSoloAcceptance(state, kernel, driver, policy, tick, changedGoalLines, changedGoalIds);
        SummarizeAcceptanceDeferrals(state, tick, changedGoalLines);

        return state.Results;
    }

    private static bool IsParallelAcceptanceLifecycleEligible(
        Goal goal,
        ConductorDriver driver)
    {
        if (!driver.ParallelAcceptanceEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(driver.ExecutionDirectory))
        {
            return true;
        }

        try
        {
            var journal = GoalOperationJournal.Read(driver.ExecutionDirectory, goal.Id);
            return !GoalOperationJournal.HasRetiredTerminalDisposition(journal) &&
                !GoalOperationJournal.HasMergeEvidenceTerminalDisposition(journal);
        }
        catch
        {
            return true;
        }
    }

    private static string BoundSingleLine(string value)
    {
        const int maxLength = 512;
        var singleLine = value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length > maxLength ? singleLine[..maxLength] : singleLine;
    }

    private static ConductorAdvanceResult EscalateParallelAcceptanceSafely(
        ConductorDriver driver,
        Goal goal,
        ConductorAutonomyPolicy policy,
        string reason)
    {
        try
        {
            return driver.EscalateParallelLandingAcceptance(goal, policy, reason);
        }
        catch (Exception escalationException)
        {
            var fallbackReason = BoundSingleLine(
                $"{reason}; escalation recording failed: {escalationException.GetType().Name}: {escalationException.Message}");
            return new ConductorAdvanceResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                policy.Name,
                new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, fallbackReason));
        }
    }

    private static string? BuildDocumentationExclusionAdmissionRecord(
        ConductorParallelAcceptanceCandidate candidate,
        IReadOnlyList<ConductorParallelAcceptanceCandidate> activeCandidates,
        int tick)
    {
        foreach (var existing in activeCandidates)
        {
            var excludedPaths = existing.GetDocumentationExclusionEvidence(candidate);
            if (excludedPaths.Count == 0)
            {
                continue;
            }

            var sample = string.Join(",", excludedPaths.Take(3));
            return $"ADMISSION tick={tick} result=admitted reason=documentation-exclusion " +
                $"goal={candidate.GoalPrefix} peer={existing.GoalPrefix} " +
                $"excludedPathCount={excludedPaths.Count} excludedPathSample={sample}";
        }

        return null;
    }

    private static void ReplayParallelAcceptanceLeaseReceipts(
        ConductorDriver driver,
        ConductorParallelAcceptanceAttempt attempt,
        List<string> changedGoalLines)
    {
        foreach (var receipt in driver.ParallelAcceptanceAttemptCoordinator.TakePendingLeaseReceipts(attempt))
        {
            RecordParallelAcceptanceProgress(receipt, changedGoalLines);
        }
    }

    internal static TerminalGoalRemedyExecutionResult ExecuteReconcileSweepAcceptanceRemedy(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        TerminalGoalRemedy remedy,
        ConductorAutonomyPolicy policy)
    {
        var mutationBlockReason = driver.LandingMutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(mutationBlockReason))
        {
            return TerminalGoalRemedyExecutionResult.Retryable(
                75,
                $"landing mutation boundary unavailable: {mutationBlockReason}");
        }

        var goal = kernel.GetGoal(remedy.GoalId);
        var candidate = TryBuildParallelAcceptanceCandidate(driver, goal, policy, slotIndex: 0, out var buildException, out _);
        if (candidate is null)
        {
            return new TerminalGoalRemedyExecutionResult(
                1,
                buildException is null
                    ? $"acceptance candidate unavailable for goal {remedy.GoalPrefix}"
                    : $"acceptance candidate unavailable: {buildException.GetType().Name}: {buildException.Message}");
        }

        ConductorParallelAcceptanceAttemptDecision decision;
        try
        {
            decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                driver.RunParallelLandingAcceptance);
            if (decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Started &&
                decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                    candidate,
                    policy,
                    driver.RunParallelLandingAcceptance);
            }
        }
        catch (AcceptanceArtifactWriterLeaseBusyException ex)
        {
            return TerminalGoalRemedyExecutionResult.Retryable(
                75,
                $"acceptance artifact writer busy; retry on next conduct tick: {ex.Message}");
        }

        if (decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
            ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            return TerminalGoalRemedyExecutionResult.Pending(
                $"background acceptance attempt {decision.Attempt.AttemptId} is {decision.Kind.ToString().ToLowerInvariant()}");
        }

        if (decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun)
        {
            var retryable = IsRetryableTerminalAttempt(decision.Attempt);
            var output =
                $"attempt={decision.Attempt.AttemptId} outcome={AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome)} " +
                $"detail={decision.Attempt.Detail ?? "no result artifact was produced"}";
            driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
            return retryable
                ? TerminalGoalRemedyExecutionResult.Retryable(75, output)
                : new TerminalGoalRemedyExecutionResult(1, output);
        }

        var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
            candidate,
            new InvalidOperationException("Completed acceptance attempt had no run result."));
        ReconcileParallelAcceptanceTerminalStateUnlessReused(kernel, goal, run, decision.Attempt);
        var completion = CompleteParallelAcceptanceRun(
            driver,
            policy,
            run,
            decision.Attempt,
            out var evidenceMutationLeaseHeld);
        MarkParallelAcceptanceReconciledUnlessLeaseHeld(
            driver,
            run,
            evidenceMutationLeaseHeld,
            decision.Attempt);
        var capturedOutput =
            $"attempt={decision.Attempt.AttemptId} outcome={AcceptanceRunDisposition(run)} " +
            $"detail={decision.Attempt.Detail ?? "no attempt detail was recorded"} completion={completion.Outcome}";

        if (run.Exception is DotnetBuildSlotsBusyException or OperationCanceledException ||
            (run.Exception is AcceptanceGateEngineException &&
                decision.Attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap) ||
            (run.Exception is (AcceptanceInfrastructureDeferredException or BuildLockBlockedException) &&
                decision.Attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap) ||
            IsEnvironmentInterferenceAcceptanceRun(run) ||
            IsIdentityStaleRegated(run, decision.Attempt) ||
            completion.IsHeld && run.EarlyResult is null)
        {
            return TerminalGoalRemedyExecutionResult.Retryable(75, capturedOutput);
        }

        return new TerminalGoalRemedyExecutionResult(
            completion.WasExecuted || completion.IsDone ? 0 : 1,
            capturedOutput);
    }

    private static string FormatSlotsBusy(DotnetBuildLeaseAcquisition.SlotsBusy slotsBusy)
    {
        var slots = string.Join(
            ",",
            slotsBusy.BusySlots.Select(slot =>
                $"slot-{slot.SlotIndex}:pid-{slot.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));
        return $"wanted-by={slotsBusy.WantedBy}; busy={slots}";
    }

    private static string FormatBuildLockBlocked(BuildLockAttribution attribution)
    {
        var holders = attribution.Holders.Count == 0
            ? "unknown"
            : string.Join(", ", attribution.Holders.Select(holder =>
                $"pid {holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} {holder.ProcessName ?? "unknown"}"));
        return $"path={attribution.Path}; holders: {holders}";
    }

    private void ReapNonTerminalEligibleGoals(
        AgentOrchestratorKernel kernel,
        string? onlyGoalId,
        HashSet<string> excludedGoals,
        HashSet<string> reapedGoals, int tick, List<string>? tickLines)
    {
        foreach (var goal in kernel.Goals)
        {
            if (onlyGoalId is not null && goal.Id.Value != onlyGoalId)
            {
                continue;
            }

            if (excludedGoals.Contains(goal.Id.Value) || IsTerminalGoal(goal))
            {
                continue;
            }

            ReapGoalOnce(kernel, goal, reapedGoals, tick, tickLines);
        }
    }

    private void DetachNonTerminalEligibleGoals(
        AgentOrchestratorKernel kernel,
        string? onlyGoalId,
        HashSet<string> excludedGoals,
        HashSet<string> reapedGoals)
    {
        foreach (var goal in kernel.Goals)
        {
            if (onlyGoalId is not null && goal.Id.Value != onlyGoalId)
            {
                continue;
            }

            if (excludedGoals.Contains(goal.Id.Value) || IsTerminalGoal(goal))
            {
                continue;
            }

            DetachGoalOnce(kernel, goal, reapedGoals);
        }
    }

    private void DetachGoalOnce(AgentOrchestratorKernel kernel, Goal goal, HashSet<string> reapedGoals)
    {
        if (!reapedGoals.Add(goal.Id.Value))
        {
            return;
        }

        _detachGoalRunningDispatches(kernel, goal);
    }

    private static bool IsTerminalGoal(Goal goal) =>
        goal.Status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static int CountParkedExcludedGoals(
        AgentOrchestratorKernel kernel,
        string? onlyGoalId,
        HashSet<string> excludedGoals) =>
        kernel.Goals.Count(goal =>
            (onlyGoalId is null || goal.Id.Value == onlyGoalId)
            && !excludedGoals.Contains(goal.Id.Value)
            && goal.Status == GoalStatus.Parked);

    private static int CountRecheckableNonTerminalGoals(
        AgentOrchestratorKernel kernel,
        string? onlyGoalId,
        IReadOnlyDictionary<string, BatchSetAsideEntry> setAsideGoals,
        BatchSetAsideCondition? condition = null,
        IReadOnlySet<string>? transientRecheckableGoalIds = null) =>
        kernel.Goals.Count(goal =>
            (onlyGoalId is null || goal.Id.Value == onlyGoalId) &&
            !IsTerminalGoal(goal) &&
            ((setAsideGoals.TryGetValue(goal.Id.Value, out var entry) &&
                (!condition.HasValue || entry.Condition == condition.Value)) ||
             (!condition.HasValue && transientRecheckableGoalIds?.Contains(goal.Id.Value) == true)));

    private static bool IsPreWalkExcludedGoal(Goal goal) =>
        goal.Status == GoalStatus.Parked || IsPreWalkExcludedTerminalGoal(goal);

    private static bool IsPreWalkExcludedTerminalGoal(Goal goal) =>
        GoalStatusSemantics.ExcludesFromConductorWorkingSet(goal.Status)
        || IsStaleTerminalGoalWithAssignedWork(goal);

    private static bool IsLoopEligibleGoal(Goal goal, ConductorDriver driver, GoalProjectionCache goalProjectionCache)
    {
        if (IsPreWalkExcludedGoal(goal))
        {
            return false;
        }

        if (goal.Status == GoalStatus.Completed &&
            !string.IsNullOrWhiteSpace(driver.ExecutionDirectory))
        {
            try
            {
                if (GoalOperationJournal.HasMergeEvidenceTerminalDisposition(
                        GoalOperationJournal.Read(driver.ExecutionDirectory, goal.Id)))
                {
                    return false;
                }
            }
            catch
            {
                // A transient journal read failure must not remove a goal from ordinary lifecycle evaluation.
            }
        }

        if (goal.Status is GoalStatus.Verifying or GoalStatus.Verified or GoalStatus.Completed)
        {
            try
            {
                var state = goalProjectionCache.ResolveState(goal, driver);
                return state != GoalLifecycleState.CleanedUp;
            }
            catch
            {
                return true;
            }
        }

        return true;
    }

    private static bool IsStaleTerminalGoalWithAssignedWork(Goal goal) =>
        (goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed) &&
        goal.Tasks.Any(task => task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman);

    private static bool IsStopRequested(string stopFilePath) =>
        !string.IsNullOrEmpty(stopFilePath) && File.Exists(stopFilePath);

    internal static WatchSleepResult SleepUntilNextTick(
        TimeSpan interval,
        string stopFilePath,
        IConductorWakeSignal? wakeSignal,
        IReadOnlyList<string> trackedExitCodePaths,
        IReadOnlyList<string>? runningAttemptExitCodePaths = null)
    {
        wakeSignal?.UpdateTrackedExitArtifacts(trackedExitCodePaths);
        if (wakeSignal is IConductorAttemptExitWakeSignal attemptWake) attemptWake.UpdateTrackedAttemptExitArtifacts(runningAttemptExitCodePaths ?? []);
        var remaining = interval;
        var poll = TimeSpan.FromSeconds(WatchStopPollIntervalSeconds);
        while (remaining > TimeSpan.Zero)
        {
            if (IsStopRequested(stopFilePath))
                return WatchSleepResult.StopRequested;
            var slice = remaining < poll ? remaining : poll;
            if (wakeSignal is not null)
            {
                if (wakeSignal.Wait(slice))
                    return WatchSleepResult.WakeSignaled;
            }
            else
            {
                Thread.Sleep(slice);
            }
            remaining -= slice;
        }

        return IsStopRequested(stopFilePath)
            ? WatchSleepResult.StopRequested
            : WatchSleepResult.FallbackElapsed;
    }

    private static string FormatOutcome(ConductorAdvanceOutcome outcome) => outcome switch
    {
        ConductorAdvanceOutcome.Executed e  => $"executed from {e.FromState} — {e.Description}",
        ConductorAdvanceOutcome.Held h      => $"held at {h.State} — {h.Reason}",
        ConductorAdvanceOutcome.Escalated e => $"escalated at {e.State} — {e.Reason}",
        ConductorAdvanceOutcome.Done d      => $"done ({d.State})",
        _                                   => outcome.ToString()!
    };
}

internal enum WatchSleepResult
{
    FallbackElapsed,
    StopRequested,
    WakeSignaled
}

internal sealed record ParallelLandingOutcome(ConductorAdvanceResult Result, int? SlotIndex);

internal sealed record GoalWalkTiming(string Goal, string Result, TimeSpan Elapsed);

internal sealed class GoalProjectionCache
{
    private readonly Dictionary<GoalId, GoalProjectionCacheEntry> _entries = [];

    internal int Count => _entries.Count;

    internal GoalLifecycleState ResolveState(Goal goal, ConductorDriver driver)
    {
        var fingerprint = BuildFingerprint(goal);
        if (_entries.TryGetValue(goal.Id, out var entry)
            && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return entry.State;
        }

        var state = GoalLifecycle.ResolveState(goal, driver.GetFacts(goal));
        _entries[goal.Id] = new GoalProjectionCacheEntry(fingerprint, state);
        return state;
    }

    internal void Invalidate(GoalId goalId) => _entries.Remove(goalId);

    internal static string BuildFingerprint(Goal goal)
    {
        var taskParts = goal.Tasks
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .Select(task =>
                string.Join(
                    ":",
                    task.Id.Value,
                    task.Status.ToString(),
                    task.LastDispatch is null ? "dispatch=none" : $"dispatch={task.LastDispatch.DispatchedAt.UtcTicks}:{task.LastDispatch.WorkerName}",
                    task.LastProcess is null ? "process=none" : $"process={task.LastProcess.IsRunning}:{task.LastProcess.CompletedAt?.UtcTicks}:{task.LastProcess.ExitCode}:{task.LastProcess.WasCancelled}",
                    task.LastVerification is null ? "verification=none" : $"verification={task.LastVerification.Succeeded}:{task.LastVerification.ExitCode}:{task.LastVerification.CompletedAt.UtcTicks}",
                    task.LastExecution is null ? "execution=none" : $"execution={task.LastExecution.StopReason}:{task.LastExecution.CompletedAt.UtcTicks}"));

        return string.Join("|", new[] { goal.Status.ToString(), $"timeline={goal.Timeline.Count}" }.Concat(taskParts));
    }
}

internal sealed record GoalProjectionCacheEntry(string Fingerprint, GoalLifecycleState State);

public sealed record BatchLoopSummary(
    int Ticks,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    int Done,
    bool StopRequested,
    ConductorLoopHandoffResult? Handoff = null,
    string? StopReason = null,
    int Rechecks = 0,
    int LandedGoals = 0);

public sealed record ConductorLoopHandoffRequest(
    int Tick,
    TimeSpan MaxDuration,
    int Done,
    int LandedGoalDelta = 0);

public sealed record ConductorLoopHandoffResult(
    bool Started,
    int? ProcessId,
    string? StdoutPath,
    string? StderrPath,
    string? Reason,
    bool Failed = false,
    string? VerificationOutcome = null,
    bool RollbackSucceeded = true)
{
    public static ConductorLoopHandoffResult StartedProcess(
        int processId,
        string stdoutPath,
        string stderrPath,
        string? verificationOutcome = null) =>
        new(true, processId, stdoutPath, stderrPath, null, Failed: false, verificationOutcome);

    public static ConductorLoopHandoffResult Skipped(string reason) =>
        new(false, null, null, null, reason);

    public static ConductorLoopHandoffResult FailedStart(
        string reason,
        string? stdoutPath,
        string? stderrPath,
        string? verificationOutcome = null,
        int? processId = null,
        bool rollbackSucceeded = false) =>
        new(false, processId, stdoutPath, stderrPath, reason, Failed: true, verificationOutcome, rollbackSucceeded);
}

public sealed record BatchTickSummary(
    int Tick,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    int Done,
    bool WatchSleeping)
{
    public IReadOnlyList<string>? ProgressLines { get; init; }
    public IReadOnlyList<ConductorOperatorDispositionSnapshot>? OperatorDispositions { get; init; }
}
