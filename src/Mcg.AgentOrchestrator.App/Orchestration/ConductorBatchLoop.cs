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
    // dashboard, builds, and acceptance gates (observed 2026-08-08). Provider cooldowns are
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
    private readonly Dictionary<string, long> _tickPhaseElapsedMs = new(StringComparer.Ordinal);
    private readonly Action<string>? _janitorialPhaseProbe;
    private readonly Func<long> _janitorialTimestamp;
    private static readonly AsyncLocal<ConductEventLogWriter?> CurrentConductEventLogWriter = new();
    private static readonly AsyncLocal<RetryDiagnosticCoalescer?> CurrentRetryDiagnostics = new();
    private static readonly object ParallelAcceptanceFairnessGate = new();
    private static string? s_parallelAcceptanceOldestWaiter;
    private static int s_parallelAcceptanceConsecutiveOvertakes;
    private static string? s_parallelAcceptanceObservedOldestWaiter;
    private static int s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks;

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
        Func<string?>? readRelaunchDrainCap = null)
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
        _readRelaunchDrainCap = readRelaunchDrainCap ?? (() => Environment.GetEnvironmentVariable(RelaunchDrainCapEnvironmentVariable));
        _writeJitter = writeJitter ?? Random.Shared.NextDouble;
        _goalReloadObservation = goalReloadObservation ?? (_ => new ConductorGoalReloadObservation.Missing());
        _blockedRecheckHeartbeatInterval = blockedRecheckHeartbeatInterval ?? DefaultBlockedRecheckHeartbeatInterval;
        _workspace = workspace;
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
        ResetLandingTickSave();
        _consecutiveJanitorialFailures.Clear();
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
            RecordSuccessfulLanding(landedGoalIds, receipt.GoalId);
            NoteLandingForTick(receipt.GoalId);
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

            if (_postLandingCanary is not null)
            {
                var canaryTask = _postLandingCanary.LaunchLandingAsync(receipt);
                lock (canaryTasksGate)
                {
                    canaryTasks.Add(canaryTask);
                }
            }

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
            TerminalGoalJournalMetadataCache.BeginMeasurement();
            var sweepClock = Stopwatch.StartNew();
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
                $"goals={kernel.Goals.Count} completed_dependencies={completedGoals.Count} set_aside={setAsideGoals.Count} dependency_metadata_ms={dependencyMetadataTiming.ElapsedMilliseconds} dependency_journals_read={dependencyMetadataTiming.JournalsRead}{FormatSweepCacheDetail(sweepResult)}{FormatSweepPhaseAttribution(_tickPhaseElapsedMs)}"));

            var preWalkClock = Stopwatch.StartNew();
            var hostedChangedGoalIds = ServiceStewardAndAuthor(kernel, onlyGoalId);
            var actionableIntentGoalIds = new HashSet<string>(StringComparer.Ordinal);
            var preWalkIntentLines = new List<string>();
            var preWalkIntentProcessed = false;
            var intentsAwaitingReload = 0;
            if (_operatorIntents is not null)
            {
                try
                {
                    actionableIntentGoalIds.UnionWith(_operatorIntents.ListActionableGoalIds());
                }
                catch (Exception ex)
                {
                    EmitProgress(
                        $"OPERATOR_INTENT result=store-unavailable phase=list reason={SanitizeReason(ex.Message)}");
                }
            }

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
                    try
                    {
                        intentResult = _operatorIntents.ExecutePending(kernel, scopedGoal);
                        if (intentResult.MutatedGoalState)
                        {
                            var invalidation = AcceptanceAttemptRetryInvalidation.Apply(
                                kernel,
                                scopedGoal,
                                driver.ParallelAcceptanceAttemptCoordinator,
                                "Operator intent made an acceptance-verified task dispatchable; invalidated the current acceptance attempt before redispatch.");
                            if (invalidation.Changed)
                            {
                                preWalkIntentLines.Add(
                                    $"ACCEPTANCE_INVALIDATED goal={ShortGoalId(scopedGoal.Id.Value)} attempt_staled={invalidation.AttemptInvalidated.ToString().ToLowerInvariant()} goal_reopened={invalidation.GoalReopened.ToString().ToLowerInvariant()}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        EmitProgress(
                            $"OPERATOR_INTENT goal={ShortGoalId(scopedGoal.Id.Value)} result=store-unavailable phase=execute reason={SanitizeReason(ex.Message)}");
                        continue;
                    }

                    preWalkIntentLines.AddRange(intentResult.ProgressLines);
                    preWalkIntentProcessed |= intentResult.ProgressLines.Count > 0;
                    if (intentResult.RejectedAdjudication)
                        preWalkIntentChangedGoalIds.Add(scopedGoal.Id);
                    if (intentResult.MutatedGoalState)
                    {
                        kernel.ClearGoalHold(scopedGoal.Id);
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
                .Where(g => IsLoopEligibleGoal(g, driver, goalProjectionCache))
                .ToArray();
            ResetScopedGoalStallCounters(eligible, unscopedDispatchableTicks);
            preWalkClock.Stop();
            preTickTimingLines.Add(FormatPhaseTiming(nextTick, "prewalk", preWalkClock.Elapsed,
                $"scoped={scopedGoals.Length} candidates={preWalkCandidates.Length} eligible={eligible.Length} deferred_intent={preWalkIntentChangedGoalIds.Count} excluded_parked={parkedExcludedCount} excluded_terminal={terminalExcludedCount} cache_entries={goalProjectionCache.Count}"));

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
            var changedGoalIds = new HashSet<GoalId>(preWalkIntentChangedGoalIds);
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
            var goalWalkClock = Stopwatch.StartNew();
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
                var singleGoalClock = Stopwatch.StartNew();
                void FinishGoalWalk(string result)
                {
                    if (!singleGoalClock.IsRunning)
                    {
                        return;
                    }

                    singleGoalClock.Stop();
                    goalWalkTimings.Add(new GoalWalkTiming(label, result, singleGoalClock.Elapsed));
                }

                // Dependency ordering is a dispatch-start gate. Do not interrupt a worker that is
                // currently in flight, but re-evaluate the edge before any later dispatch starts.
                var depHoldReason = HasStartedGoalWork(goal)
                    ? null
                    : GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel);
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
                        ReapGoalOnce(kernel, goal, reapedGoals);
                        SetAside(kernel, driver, goal, BatchSetAsideCondition.DependencyEscalated, setAsideGoals, selfClearedSetAsideEntries);
                        tickEscalated++;
                    }
                    else
                    {
                        TrackGoalHold(
                            kernel,
                            goal,
                            TryResolveLifecycleState(goalProjectionCache, driver, goal),
                            depHoldReason,
                            _utcNow(),
                            effectiveGoalStallThreshold,
                            changedGoalIds,
                            tickLines);
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
                        tickLines);
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
                    ReapGoalOnce(kernel, goal, reapedGoals);
                    SetAside(kernel, driver, goal, BatchSetAsideCondition.LifecycleEscalation, setAsideGoals, selfClearedSetAsideEntries, sweepResult);
                    tickEscalated++;
                    FinishGoalWalk("verified-escalation");
                    continue;
                }

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
                            retryAdvanceFaulted = true;
                            break;
                        }
                    }

                    if (retryAdvanceFaulted)
                        continue;
                }

                if (TryReconcileAwaitingVerificationHold(kernel, goal, result, totalTicks, out var reconciledOutcome))
                {
                    changedGoalIds.Add(goal.Id);
                    result = result with { Outcome = reconciledOutcome };
                    goalProjectionCache.Invalidate(goal.Id);
                }

                TrackGoalOutcome(
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
                    // Held goals have no kernel state mutation worth a per-goal CAS write.
                    if (!result.IsHeld)
                        changedGoalIds.Add(goal.Id);
                    Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → {FormatOutcome(result.Outcome)}");
                    kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {FormatOutcome(result.Outcome)}",
                        VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(result.Outcome));
                }

                if (result.WasExecuted)        { tickAdvanced++; }
                else if (result.IsHeld)        { tickHeld++; }
                else if (result.WasEscalated)  { tickEscalated++; escalatedGoals.Add(goal.Id.Value); ReapGoalOnce(kernel, goal, reapedGoals); SetAside(kernel, driver, goal, GetSetAsideCondition(result), setAsideGoals, selfClearedSetAsideEntries, sweepResult); }
                else if (result.IsDone)        { tickDone++;      completedGoals.Add(goal.Id.Value); excludedGoals.Add(goal.Id.Value); }
                goalProjectionCache.Invalidate(goal.Id);
                FinishGoalWalk(result.Outcome.GetType().Name);
            }
            goalWalkClock.Stop();
            driver.PhaseTimingSink = previousPhaseTimingSink;
            foreach (var line in perGoalPhaseTimingLines)
            {
                EmitProgress(line, tickLines);
            }

            EmitProgress(FormatPhaseTiming(totalTicks, "per-goal-walk", goalWalkClock.Elapsed,
                $"goals={goalWalkTimings.Count} slowest={FormatSlowestGoalWalks(goalWalkTimings)}"), tickLines);

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
                foreach (var goal in eligible)
                {
                    var activeTask = ConductorWatchProgressReporter.GetActiveTask(goal);
                    var cachedLiveChanges = activeTask?.LastDispatch is { } dispatch &&
                        liveChangeSnapshots.TryGetValue((dispatch.WorkingDirectory, dispatch.BaseCommit), out var snapshot)
                            ? snapshot
                            : null;
                    foreach (var line in _watchProgressReporter.BuildLines(
                        goal,
                        quiet,
                        policy,
                        watchInterval,
                        stallWarningThreshold,
                        cachedLiveChanges))
                    {
                        EmitProgress(line, tickLines);
                    }
                }
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
                EmitProgress($"{summaryPrefix} tick={totalTicks} advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} done={tickDone}", tickLines);
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
            if (pendingSelfRelaunch is not null)
                SaveLandingTickBeforeRelaunch(kernel, checkpointGoalTick, persistGoalTick, persistTick, checkpointHeldGoals, totalTicks, busyWriteDelay);

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
                    $"LOOP_STOP tick={totalTicks} rechecks={totalBlockedRechecks} reason=unintended-exit {detail}");
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

            var reason = $"dispatch-record-write-{disposition}-limit count={skips}/{DispatchRecordContentionSkipLimit} sqliteCode={code}";
            changedGoalLines.Add($"GOAL goal={label} result=escalated reason={reason}");
            lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
            changedGoalIds.Add(goal.Id);
            dispatchRecordWriteBoundGoalIds.Add(goal.Id);
            kernel.ClearGoalHold(goal.Id);
            kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {reason}");
            escalatedGoals.Add(goal.Id.Value);
            ReapGoalOnce(kernel, kernel.GetGoal(goal.Id), reapedGoals);
            SetAside(kernel, driver, kernel.GetGoal(goal.Id), BatchSetAsideCondition.AdvanceFault, setAsideGoals, selfClearedSetAsideEntries);
            tickEscalated++;
            finishGoalWalk($"dispatch-record-{disposition}-limit");
            result = null!;
            return false;
        }
        catch (Exception ex)
        {
            var msg = $"Batch loop tick {totalTicks}: fault isolating goal — advance threw: {SanitizeReason(ex.Message)}";
            changedGoalLines.Add($"GOAL goal={label} result=escalated reason={SanitizeReason(ex.Message)}");
            lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
            changedGoalIds.Add(goal.Id);
            kernel.ClearGoalHold(goal.Id);
            Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated (advance threw): {ex.Message}");
            kernel.RecordGoalPolicyDecision(goal.Id, msg);
            escalatedGoals.Add(goal.Id.Value);
            ReapGoalOnce(kernel, goal, reapedGoals);
            SetAside(kernel, driver, goal, BatchSetAsideCondition.AdvanceFault, setAsideGoals, selfClearedSetAsideEntries);
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

    // Emit a compact progress line to stdout with immediate flush; optionally accumulate in a list.
    private static void EmitProgress(string line, List<string>? accumulator = null)
    {
        var stampedLine = $"{line} ts={DateTimeOffset.UtcNow:O}";
        Console.WriteLine(stampedLine);
        Console.Out.Flush();
        accumulator?.Add(stampedLine);
        TryAppendConductEvent(line);
    }

    private static void TryAppendConductEvent(string line)
    {
        var writer = CurrentConductEventLogWriter.Value;
        if (writer is null || !TryClassifyConductEvent(line, out var kind, out var goalId))
            return;

        var required = kind is "loop-start-deferred" or "loop-relaunch-rollback" or "loop-janitorial-failure" or "loop-janitorial-degraded" or "goal-stalled" or "sweep-blocker" or "sweep-owned-root-deferred" or
            "sweep-remedy-attempt" or "sweep-remedy-result" or "sweep-escalation" or "exit-unapplied" or
            "blocked-recheck-heartbeat" or "policy-reload-failed" ||
            line.StartsWith("LOOP_HANDOFF_FAILED ", StringComparison.Ordinal);
        try
        {
            if (required)
            {
                if (!writer.AppendRequired(kind, goalId, line))
                {
                    Console.Error.WriteLine(
                        $"LOOP_EVENT_STREAM_WRITE_PENDING eventKind={kind} goal={goalId ?? "none"} " +
                        $"pending=true detail={SanitizeHandoffDetail(line)}");
                    Console.Error.Flush();
                }
            }
            else
            {
                writer.Append(kind, goalId, line);
            }
        }
        catch when (!required)
        {
            // Shared operator event streaming is advisory; stdout remains the primary conduct log.
        }
    }

    private static bool TryClassifyConductEvent(string line, out string kind, out string? goalId)
    {
        goalId = TryExtractToken(line, "goal=");
        var head = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        kind = head switch
        {
            "ACCEPTANCE" => "acceptance",
            "ACCEPTANCE_LEASE_ACQUIRE" => "acceptance-lease",
            "ACCEPTANCE_LEASE_HANDOFF" => "acceptance-lease",
            "ACCEPTANCE_LEASE_PERMIT_RELEASE" => "acceptance-lease",
            "ACCEPTANCE_LEASE_RELEASE" => "acceptance-lease",
            "ACCEPTANCE_LEASE_YIELD" or "ACCEPTANCE_LEASE_DEGRADE" => "acceptance-lease",
            "BUILD_LOCK_BLOCKED" => "lock-blocker",
            // Named from the emitter so the operator-visible token and its classification cannot drift.
            ConductorUnappliedExitWatch.EventName => "exit-unapplied",
            "GOAL" => ClassifyGoalEvent(line),
            "GOAL_STALLED" => "goal-stalled",
            "LOCK" => "lock-blocker",
            "LOOP_HANDOFF" => "loop-handoff",
            "LOOP_HANDOFF_FAILED" => "loop-handoff",
            "LOOP_HANDOFF_PENDING" => "loop-handoff",
            "LOOP_HANDOFF_SKIPPED" => "loop-handoff",
            "LOOP_RELAUNCH_SCHEDULED" => "loop-relaunch",
            "LOOP_RELAUNCH_NOT_REQUIRED" => "loop-relaunch",
            "LOOP_RELAUNCH_DRAIN" or "LOOP_RELAUNCH_DETACH" => "loop-relaunch",
            "LOOP_RELAUNCH_REBUILD" => "loop-relaunch",
            "LOOP_RELAUNCH_ROLLBACK" => "loop-relaunch-rollback",
            "LOOP_JANITORIAL_FAILED" => "loop-janitorial-failure",
            "LOOP_JANITORIAL_DEGRADED" => "loop-janitorial-degraded",
            "LOOP_JANITORIAL_RECOVERED" => "loop-janitorial-recovered",
            "LOOP_JANITORIAL_RETRYING" => "loop-janitorial-retry",
            "LOOP_JANITORIAL_RETRY_SUCCEEDED" => "loop-janitorial-retry",
            "LOOP_START" => "loop-start",
            "LOOP_START_DEFERRED" => "loop-start-deferred",
            "LOOP_STOP_DEFERRED" => "loop-stop-deferred",
            "LOOP_STOP" => "loop-stop",
            "POLICY_RELOAD" => "policy-reload",
            "POLICY_RELOAD_FAILED" => "policy-reload-failed",
            "POLICY_WARNING" => "policy-warning",
            "SPECULATIVE_COHORT_PLAN" => "speculative-cohort-plan",
            "TRAIN_RECEIPT_STALE" => "train-receipt-stale",
            "ACCEPTANCE_COHORT" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_ENTRY" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_EXIT" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_INFLIGHT" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_FAIRNESS" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_FAIRNESS_TRANSITION" => "acceptance-cohort",
            "SWEEP_BLOCKER" => "sweep-blocker", "SWEEP_OWNED_ROOT_DEFERRED" => "sweep-owned-root-deferred",
            "SWEEP_ESCALATION" => "sweep-escalation",
            "SWEEP_REMEDY_ATTEMPT" => "sweep-remedy-attempt",
            "SWEEP_REMEDY_RESULT" => "sweep-remedy-result", "SWEEP_OWNED_ROOT_OBSERVED" => "sweep-owned-root-observed",
            "SWEEP_GOAL_ROOT_RECLAIMED" => "sweep-goal-root-reclaimed",
            "SWEEP_GOAL_ROOT_RECLAIM_FAILED" => "sweep-goal-root-reclaim-failed",
            "SET_ASIDE_SELF_CLEARED" => "set-aside-self-cleared",
            "BLOCKED_RECHECK_HEARTBEAT" => "blocked-recheck-heartbeat",
            "TICK_WRITE_BUSY" => "lock-blocker",
            "TICK_WRITE_DEGRADED" => "lock-blocker",
            "GLANCE" => "progressive-review-glance",
            "WATCH_TRANSITION" => "watch-transition",
            _ => string.Empty
        };

        return kind.Length > 0;
    }

    private sealed record BlockedRecheckRecurrence(
        string GoalId,
        string DisplayKey,
        string Fingerprint,
        int Count,
        string Evidence,
        string Command);

    private static void UpdateBlockedRecheckRecurrences(
        TerminalGoalSweepResult? sweepResult,
        IReadOnlyDictionary<string, BatchSetAsideEntry> setAsideGoals,
        Dictionary<string, BlockedRecheckRecurrence> recurrences)
    {
        var current = sweepResult?.Goals
            .SelectMany(goal => goal.Blockers.Select(blocker => new
            {
                Key = $"{goal.GoalId.Value}:{blocker.Kind}",
                GoalId = goal.GoalId.Value,
                DisplayKey = $"{goal.GoalPrefix}:{blocker.Kind}",
                Fingerprint = $"{blocker.Evidence}\n{blocker.Command}",
                blocker.Evidence,
                blocker.Command
            }))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    GoalId = group.Select(item => item.GoalId).First(),
                    DisplayKey = group.Select(item => item.DisplayKey).First(),
                    Fingerprint = string.Join("\n", group.Select(item => item.Fingerprint).OrderBy(value => value, StringComparer.Ordinal)),
                    Evidence = string.Join("; ", group.Select(item => item.Evidence).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)),
                    Command = string.Join("; ", group.Select(item => item.Command).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                },
                StringComparer.Ordinal);
        current ??= [];

        foreach (var staleKey in recurrences.Keys.Except(current.Keys, StringComparer.Ordinal).ToArray())
        {
            var previous = recurrences[staleKey];
            var explicitlySwept = sweepResult?.ExplicitlySweptGoalIds.Any(goalId =>
                string.Equals(goalId.Value, previous.GoalId, StringComparison.Ordinal)) == true;
            if (!setAsideGoals.ContainsKey(previous.GoalId) || explicitlySwept)
            {
                recurrences.Remove(staleKey);
            }
            // Otherwise the goal was not observed by this sweep. Keep the last known condition
            // visible, but do not claim another recurrence without a fresh observation.
        }

        foreach (var (key, observation) in current)
        {
            recurrences[key] = recurrences.TryGetValue(key, out var previous) &&
                string.Equals(previous.Fingerprint, observation.Fingerprint, StringComparison.Ordinal)
                    ? previous with
                    {
                        Count = previous.Count + 1,
                        Evidence = observation.Evidence,
                        Command = observation.Command
                    }
                    : new BlockedRecheckRecurrence(
                        observation.GoalId,
                        observation.DisplayKey,
                        observation.Fingerprint,
                        1,
                        observation.Evidence,
                        observation.Command);
        }
    }

    private static string FormatBlockedRecheckHeartbeat(
        IReadOnlyDictionary<string, BlockedRecheckRecurrence> recurrences,
        int totalBlockedRechecks)
    {
        var blocked = recurrences
            .OrderBy(entry => entry.Value.DisplayKey, StringComparer.Ordinal)
            .Select(entry =>
                $"{entry.Value.DisplayKey}(recurrences={entry.Value.Count})" +
                $"[evidence={SanitizeHeartbeatDetail(entry.Value.Evidence)}|clears={SanitizeHeartbeatDetail(entry.Value.Command)}]")
            .ToArray();
        return $"BLOCKED_RECHECK_HEARTBEAT rechecks={totalBlockedRechecks} blocked={string.Join(',', blocked)}";
    }

    private static string ClassifyGoalEvent(string line)
    {
        if (line.Contains("result=done", StringComparison.Ordinal) ||
            line.Contains("result=landed", StringComparison.Ordinal))
            return "goal-landing";
        if (line.Contains("result=escalated", StringComparison.Ordinal) ||
            line.Contains("escalated", StringComparison.Ordinal))
            return "goal-escalation";
        if (line.Contains("result=", StringComparison.Ordinal))
            return "goal";
        return string.Empty;
    }

    private static string? TryExtractToken(string line, string prefix)
    {
        var start = line.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += prefix.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static string FormatPhaseTiming(int tick, string phase, TimeSpan elapsed, string detail) =>
        $"PHASE_TIMING tick={tick} phase={phase} elapsed_ms={(long)elapsed.TotalMilliseconds} {detail}";

    private static string FormatSweepCacheDetail(TerminalGoalSweepResult? result) =>
        result is null
            ? " sweep_git_index_ms=0 sweep_evidence_ms=0 sweep_ephemeral_ms=0 sweep_attention_ms=0 sweep_merge_evidence_ms=0 sweep_goals_ms=0 sweep_git_spawns=0 sweep_goals_swept=0"
            : $" sweep_cache_hits={result.CacheHitCount} sweep_cache_misses={result.CacheMissCount} sweep_git_index_ms={result.GitIndexDurationMs} sweep_evidence_ms={result.EvidenceDurationMs} sweep_ephemeral_ms={result.EphemeralDurationMs} sweep_attention_ms={result.AttentionDurationMs} sweep_merge_evidence_ms={result.MergeEvidenceDurationMs} sweep_goals_ms={result.GoalsDurationMs} sweep_git_spawns={result.GitSpawnCount} sweep_goals_swept={result.GoalsSweptCount}";

    private static string FormatSweepPhaseAttribution(IReadOnlyDictionary<string, long> elapsedByPhase) =>
        $" terminal_sweep_ms={elapsedByPhase.GetValueOrDefault("sweep")}" +
        $" persist_terminalizations_ms={elapsedByPhase.GetValueOrDefault("persist-sweep-terminalizations")}" +
        $" recover_dispatches_ms={elapsedByPhase.GetValueOrDefault("recover-interrupted-dispatches")}" +
        $" count_dispatches_ms={elapsedByPhase.GetValueOrDefault("count-running-dispatches")}" +
        $" self_relaunch_drain_ms={elapsedByPhase.GetValueOrDefault("self-relaunch-drain")}" +
        $" canary_await_ms={elapsedByPhase.GetValueOrDefault("await-canary-tasks")}" +
        $" readmit_setaside_ms={elapsedByPhase.GetValueOrDefault("readmit-resolved-set-aside-goals")}" +
        $" mark_dependencies_ms={elapsedByPhase.GetValueOrDefault("mark-completed-dependency-goals")}" +
        $" reconcile_unscoped_ms={elapsedByPhase.GetValueOrDefault("reconcile-unscoped-dispatchable-goals")}";

    private static HashSet<string> GetCompletedGoalIds(AgentOrchestratorKernel kernel) =>
        kernel.Goals
            .Where(goal => goal.Status == GoalStatus.Completed)
            .Select(goal => goal.Id.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string FormatSlowestGoalWalks(IReadOnlyList<GoalWalkTiming> timings)
    {
        var slowest = timings
            .OrderByDescending(timing => timing.Elapsed)
            .Take(5)
            .Select(timing => $"{timing.Goal}:{(long)timing.Elapsed.TotalMilliseconds}ms:{Sanitize(timing.Result)}")
            .ToArray();
        return slowest.Length == 0 ? "none" : string.Join("|", slowest);
    }

    internal static void PersistCriticalDispatchStartOrThrow(
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persistGoalTick,
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRecordCheckpointPhase checkpointPhase = DispatchRecordCheckpointPhase.BeforeProcessStart)
    {
        var tickLines = new List<string>();
        PersistWriteAttemptResult persistResult;
        try
        {
            persistResult = TryPersistWithBusyContainmentResult(
                () => persistGoalTick(kernel, [goalId]),
                tick: 0,
                goals: ShortGoalId(goalId.Value),
                kind: "dispatch-start",
                tickLines,
                busyWriteDelay: null,
                allowLegacyMessageClassification: false);
        }
        catch (Exception ex)
        {
            var failure = DispatchRecordWriteException.From(
                ex,
                checkpointPhase,
                "dispatch-start",
                goalId,
                taskId);
            if (failure.IsFatal)
                EmitProgress(failure.Message);
            throw failure;
        }

        if (persistResult.Succeeded)
            return;

        var contentionFailure = DispatchRecordWriteException.From(
            persistResult.ContentionFailure!,
            checkpointPhase,
            "dispatch-start",
            goalId,
            taskId);
        if (contentionFailure.IsFatal)
            EmitProgress(contentionFailure.Message);
        throw contentionFailure;
    }

    private void CompletePersistedOperatorIntents(
        IReadOnlyCollection<GoalId> persistedGoalIds,
        List<string> tickLines)
    {
        if (_operatorIntents is null)
        {
            return;
        }

        try
        {
            _operatorIntents.CompletePersisted(persistedGoalIds);
        }
        catch (Exception ex)
        {
            var line =
                $"OPERATOR_INTENT goals={ResolveGoalContext(persistedGoalIds, onlyGoalId: null)} result=completion-deferred reason={SanitizeReason(ex.Message)}";
            EmitProgress(line, tickLines);
        }
    }

    private static IReadOnlyCollection<GoalId> ApplyCheckpointOutcomes(
        IReadOnlyList<GoalSnapshotCheckpointResult> outcomes,
        IReadOnlyCollection<GoalId> requestedGoalIds,
        Dictionary<string, GoalSnapshotCheckpointResult> heldGoals,
        int tick,
        string kind,
        List<string> tickLines,
        bool deferEmission = false)
    {
        var byGoal = outcomes.ToDictionary(outcome => outcome.GoalId, StringComparer.Ordinal);
        var durable = new List<GoalId>(requestedGoalIds.Count);
        foreach (var goalId in requestedGoalIds)
        {
            if (!byGoal.TryGetValue(goalId.Value, out var outcome))
            {
                throw new InvalidOperationException(
                    $"Checkpoint persistence returned no disposition for goal {ShortGoalId(goalId.Value)}.");
            }

            var goal = ShortGoalId(goalId.Value);
            if (outcome.IsDurable)
            {
                durable.Add(goalId);
                if (heldGoals.Remove(goalId.Value, out var heldOutcome))
                {
                    var line =
                        $"TICK_CHECKPOINT_RECOVERED tick={tick} kind={kind} goal={goal} store={Sanitize(heldOutcome.Store)} " +
                        $"database={SanitizeReceiptToken(heldOutcome.DatabasePath)} operation={SanitizeReceiptToken(heldOutcome.Operation)} " +
                        $"sqlite_code={heldOutcome.SqliteErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                        $"sqlite_extended_code={heldOutcome.SqliteExtendedErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                        $"attempt={outcome.AttemptCount} elapsed_ms={heldOutcome.ElapsedMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} disposition=recovered";
                    if (deferEmission)
                        tickLines.Add(line);
                    else
                        EmitProgress(line, tickLines);
                }
                continue;
            }

            var firstHoldReceipt = !heldGoals.ContainsKey(goalId.Value);
            heldGoals[goalId.Value] = outcome;
            var eventName = firstHoldReceipt ? "TICK_CHECKPOINT_HOLD" : "TICK_CHECKPOINT_RETRY";
            var disposition = firstHoldReceipt ? "exhausted-held" : "still-held";
            var holdLine =
                $"{eventName} tick={tick} kind={kind} goal={goal} store={Sanitize(outcome.Store)} " +
                $"database={SanitizeReceiptToken(outcome.DatabasePath)} operation={SanitizeReceiptToken(outcome.Operation)} " +
                $"sqlite_code={outcome.SqliteErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                $"sqlite_extended_code={outcome.SqliteExtendedErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                $"attempt={outcome.AttemptCount} elapsed_ms={outcome.ElapsedMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                $"disposition={disposition} holder=unknown";
            if (deferEmission)
                tickLines.Add(holdLine);
            else
                EmitProgress(holdLine, tickLines);
        }

        return durable;
    }

    private static void PersistGoalTickOrThrow(
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persistGoalTick,
        AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> changedGoalIds,
        int tick,
        List<string> tickLines,
        Action<TimeSpan>? busyWriteDelay)
    {
        var goals = ResolveGoalContext(changedGoalIds, onlyGoalId: null);
        if (!TryPersistGoalTick(
                persistGoalTick,
                kernel,
                changedGoalIds,
                tick,
                "goal",
                tickLines,
                busyWriteDelay))
        {
            ThrowCriticalPersistFailure("goal", goals, taskId: null);
        }
    }

    private static bool TryPersistGoalTick(
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persistGoalTick,
        AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> changedGoalIds,
        int tick,
        string kind,
        List<string> tickLines,
        Action<TimeSpan>? busyWriteDelay) =>
        TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, changedGoalIds),
            tick,
            ResolveGoalContext(changedGoalIds, onlyGoalId: null),
            kind,
            tickLines,
            busyWriteDelay);

    private static void ThrowCriticalPersistFailure(string kind, string goals, TaskId? taskId)
    {
        var task = taskId is null ? "" : $" task={ShortGoalId(taskId.Value)}";
        var message = $"DISPATCH_RECORD_WRITE_FAILED kind={kind} goal={goals}{task} error=sqlite-busy-retry-exhausted";
        EmitProgress(message);
        throw new InvalidOperationException(message);
    }

    private static bool TryPersistTick(
        Action<AgentOrchestratorKernel>? persistTick,
        AgentOrchestratorKernel kernel,
        int tick,
        string goals,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null)
    {
        if (persistTick is null)
        {
            return true;
        }

        return TryPersistWithBusyContainment(
            () => persistTick(kernel),
            tick,
            goals,
            kind,
            tickLines,
            busyWriteDelay,
            diagnosticAttempt);
    }

    private static bool TryPersistCheckpoint(
        Action<AgentOrchestratorKernel>? persistTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        AgentOrchestratorKernel kernel,
        int tick,
        string? onlyGoalId,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick = null,
        Dictionary<string, GoalSnapshotCheckpointResult>? checkpointHeldGoalIds = null)
    {
        if (checkpointGoalTick is null && persistGoalTick is null)
        {
            return TryPersistTick(
                persistTick,
                kernel,
                tick,
                ResolveGoalContext(kernel, onlyGoalId),
                kind,
                tickLines,
                busyWriteDelay,
                diagnosticAttempt);
        }

        var goalIds = ResolveCheckpointGoalIds(kernel, onlyGoalId);
        if (goalIds.Length == 0)
        {
            return true;
        }

        if (checkpointGoalTick is not null)
        {
            ArgumentNullException.ThrowIfNull(checkpointHeldGoalIds);
            var lines = tickLines ?? [];
            return ApplyCheckpointOutcomes(
                checkpointGoalTick(kernel, goalIds),
                goalIds,
                checkpointHeldGoalIds,
                tick,
                kind,
                lines).Count == goalIds.Length;
        }

        return TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, goalIds),
            tick,
            ResolveGoalContext(goalIds, onlyGoalId),
            kind,
            tickLines,
            busyWriteDelay,
            diagnosticAttempt);
    }

    private void PersistGracefulDetachCheckpoint(
        Action<AgentOrchestratorKernel>? persistTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        AgentOrchestratorKernel kernel,
        int tick,
        string? onlyGoalId,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoalIds)
    {
        if (checkpointGoalTick is not null)
        {
            if (!TryPersistCheckpoint(
                    persistTick,
                    persistGoalTick,
                    kernel,
                    tick,
                    onlyGoalId,
                    kind,
                    tickLines,
                    busyWriteDelay,
                    checkpointGoalTick: checkpointGoalTick,
                    checkpointHeldGoalIds: checkpointHeldGoalIds))
            {
                EmitProgress(
                    $"TICK_WRITE_DETACH_CHECKPOINT_DEFERRED tick={tick} kind={kind} goal={ResolveGoalContext(kernel, onlyGoalId)} disposition=held",
                    tickLines);
            }
            return;
        }

        for (var attempt = 1; attempt <= DefaultGracefulDetachCheckpointAttempts; attempt++)
        {
            if (TryPersistCheckpoint(
                    persistTick,
                    persistGoalTick,
                    kernel,
                    tick,
                    onlyGoalId,
                    kind,
                    tickLines,
                    busyWriteDelay,
                    attempt))
            {
                CompleteWriteRetryDiagnostics(ResolveGoalContext(kernel, onlyGoalId), kind, tickLines);
                return;
            }

            if (attempt == DefaultGracefulDetachCheckpointAttempts)
            {
                EmitProgress(
                    $"TICK_WRITE_DETACH_CHECKPOINT_FAILED tick={tick} kind={kind} goal={ResolveGoalContext(kernel, onlyGoalId)} attempts={attempt} recoveryEvidence=spawn-registry-lifecycle",
                    tickLines);
                CompleteWriteRetryDiagnostics(ResolveGoalContext(kernel, onlyGoalId), kind, tickLines);
                return;
            }

            var goalContext = ResolveGoalContext(kernel, onlyGoalId);
            EmitRetryDiagnostic(
                "TICK_WRITE_RETRYING",
                goalContext,
                kind,
                $"TICK_WRITE_RETRYING tick={tick} kind={kind} goal={goalContext} attempt={attempt} reason=graceful-detach-checkpoint-required",
                tickLines);
            var delay = RetryLoopPolicy.GetWriteDelay(attempt, _writeJitter());
            if (busyWriteDelay is null)
                Thread.Sleep(delay);
            else
                busyWriteDelay(delay);
        }

    }

    private static GoalId[] ResolveCheckpointGoalIds(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        var goals = onlyGoalId is null
            ? kernel.Goals.Where(goal => !IsTerminalGoal(goal))
            : kernel.Goals.Where(goal => goal.Id.Value == onlyGoalId);
        return goals.Select(goal => goal.Id).ToArray();
    }

    private static bool TryPersistWithBusyContainment(
        Action persist,
        int tick,
        string goals,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null)
        => TryPersistWithBusyContainmentResult(
            persist,
            tick,
            goals,
            kind,
            tickLines,
            busyWriteDelay,
            diagnosticAttempt).Succeeded;

    private static PersistWriteAttemptResult TryPersistWithBusyContainmentResult(
        Action persist,
        int tick,
        string goals,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null,
        bool allowLegacyMessageClassification = true)
    {
        var delay = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; attempt <= DefaultMaxBusyWriteAttempts; attempt++)
        {
            try
            {
                persist();
                return PersistWriteAttemptResult.Success;
            }
            catch (Exception ex) when (IsTransientSqliteLock(ex, allowLegacyMessageClassification))
            {
                var reportedAttempt = diagnosticAttempt ?? attempt;
                EmitRetryDiagnostic(
                    "TICK_WRITE_BUSY",
                    goals,
                    kind,
                    $"TICK_WRITE_BUSY tick={tick} kind={kind} goal={goals} attempt={reportedAttempt} holder=unknown",
                    tickLines);

                if (attempt == DefaultMaxBusyWriteAttempts)
                {
                    EmitRetryDiagnostic(
                        "TICK_WRITE_DEGRADED",
                        goals,
                        kind,
                        $"TICK_WRITE_DEGRADED tick={tick} kind={kind} goal={goals} attempt={reportedAttempt} disposition=exhausted holder=unknown error={SanitizeReason(ex.Message)}",
                        tickLines);
                    return new PersistWriteAttemptResult(false, ex);
                }

                if (busyWriteDelay is null)
                    Thread.Sleep(delay);
                else
                    busyWriteDelay(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }

        throw new UnreachableException("The bounded persistence loop must return on success or final contention.");
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

    private static bool IsTransientSqliteLock(Exception ex, bool allowLegacyMessageClassification = true)
    {
        if (DispatchRecordWriteException.IsSqliteBusyOrLocked(ex))
        {
            return true;
        }

        if (allowLegacyMessageClassification
            && LegacySqliteLockMessageClassifier.IsBusyOrLocked(ex))
        {
            return true;
        }

        return ex.InnerException is not null
            && IsTransientSqliteLock(ex.InnerException, allowLegacyMessageClassification);
    }

    private readonly record struct PersistWriteAttemptResult(bool Succeeded, Exception? ContentionFailure)
    {
        internal static PersistWriteAttemptResult Success => new(true, null);
    }

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

    private static void TrackGoalOutcome(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAdvanceOutcome outcome,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        HashSet<GoalId> changedGoalIds,
        List<string> tickLines)
    {
        if (outcome is ConductorAdvanceOutcome.Held held)
        {
            if (driver.SliceBatchParentExecutionGuard?.IsSliceBatchParent(goal) == true)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            if (held.State is GoalLifecycleState.Running
                or GoalLifecycleState.AwaitingVerification
                or GoalLifecycleState.Verifying)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            TrackGoalHold(
                kernel,
                goal,
                held.State.ToString(),
                held.Reason,
                observedAt,
                stallThreshold,
                changedGoalIds,
                tickLines,
                held.StableIdentity);
            return;
        }

        ClearGoalHold(kernel, goal, changedGoalIds);
    }

    private static void TrackGoalHold(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string state,
        string blocker,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        HashSet<GoalId> changedGoalIds,
        List<string> tickLines,
        string? stableIdentity = null)
    {
        try
        {
            var observation = kernel.ObserveGoalHold(
                goal.Id,
                state,
                blocker,
                observedAt,
                stallThreshold,
                stableIdentity);
            if (observation.StateChanged)
            {
                changedGoalIds.Add(goal.Id);
            }

            if (!observation.BecameStalled)
            {
                return;
            }

            var repeatedForSeconds = Math.Max(
                0,
                (long)(observedAt - observation.Hold.StartedAt).TotalSeconds);
            EmitProgress(
                $"GOAL_STALLED goal={goal.Id.Value[..8]} state={Sanitize(state)} " +
                $"repeatedForSeconds={repeatedForSeconds} blocker={FormatStalledBlockerDetail(blocker)}",
                tickLines);
        }
        catch (Exception ex)
        {
            // The watchdog is diagnostic safety infrastructure. A persistence or event-stream
            // failure here must not take down the conductor loop it is meant to protect.
            try
            {
                Console.Error.WriteLine(
                    $"GOAL_STALL_TRACKING_FAILED goal={goal.Id.Value[..8]} " +
                    $"exception={ex.GetType().Name} message={SanitizeHandoffDetail(ex.Message)}");
                Console.Error.Flush();
            }
            catch
            {
                // Console diagnostics are best effort during fault isolation.
            }
        }
    }

    private static void ClearGoalHold(
        AgentOrchestratorKernel kernel,
        Goal goal,
        HashSet<GoalId> changedGoalIds)
    {
        if (kernel.ClearGoalHold(goal.Id))
        {
            changedGoalIds.Add(goal.Id);
        }
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

        var results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        var deferredByAdmission = 0;
        var orderedEligible = OrderParallelAcceptanceEligibleGoals(eligible
            .Where(goal =>
                IsParallelAcceptanceLifecycleEligible(goal, driver) &&
                goal.Status is GoalStatus.Verified or GoalStatus.Verifying &&
                AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal) &&
                !ConductorDriver.HasPendingDeferredNoChangeEvidence(goal) &&
                GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel) is null &&
                VerifiedAcceptanceEscalationDecision.TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver) == false)
            .ToArray());
        var activeReservations = BuildActiveParallelAcceptanceReservations(
            driver.ParallelAcceptanceAttemptCoordinator,
            kernel.Goals.Where(goal => goal.Status != GoalStatus.Completed).ToArray());
        if (activeReservations.Failure is { } capacityFailure)
        {
            var reason =
                $"acceptance capacity state unavailable; retry on next conduct tick: {SanitizeReason(capacityFailure.Message)}";
            foreach (var goal in orderedEligible)
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(goal, policy, reason),
                    null);
            }

            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=held reason=acceptance-capacity-state-unavailable detail={SanitizeReason(capacityFailure.Message)}",
                changedGoalLines);
            return results;
        }

        var liveAttempts = activeReservations.Attempts.ToList();
        var activeCandidates = activeReservations.Candidates;
        var activeAttemptIds = activeReservations.AttemptIds;
        var activeAttemptSlotIndexes = activeReservations.StableSlotIndexes;
        foreach (var goal in orderedEligible)
        {
            try
            {
            var sameGoalAttempts = driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                [goal.Id.Value]);
            if (sameGoalAttempts.Count == 0)
            {
                continue;
            }

            var observed = sameGoalAttempts
                .Select(attempt =>
                {
                    var persistedCandidate = ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        attempt.SlotIndex,
                        attempt.ScopePaths ?? [],
                        attempt.BranchHeadSha,
                        attempt.MainHeadSha);
                    return driver.ParallelAcceptanceAttemptCoordinator.ObserveExistingAttempt(
                        attempt,
                        persistedCandidate);
                })
                .OrderByDescending(decision => decision.Attempt.StartedAt)
                .ThenByDescending(decision => decision.Attempt.AttemptId, StringComparer.Ordinal)
                .ToArray();
            var running = observed
                .Where(decision => decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Running)
                .ToArray();
            var terminal = observed
                .Where(decision => decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                    ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun)
                .ToArray();

            // Terminal siblings release only their own durable claims. A single newest terminal
            // owns the goal-level transition when every same-goal producer is terminal; otherwise
            // every terminal drains and the remaining live producer keeps the goal held.
            var retainedTerminal = running.Length == 0 ? terminal.FirstOrDefault() : null;
            foreach (var terminalSibling in terminal.Where(decision =>
                         retainedTerminal is null ||
                         !string.Equals(
                             decision.Attempt.AttemptId,
                             retainedTerminal.Attempt.AttemptId,
                             StringComparison.Ordinal)))
            {
                driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(terminalSibling.Attempt);
            }

            if (running.Length > 0)
            {
                results[goal.Id.Value] = ReserveRunningParallelAcceptanceAttempts(
                    kernel,
                    driver,
                    goal,
                    policy,
                    tick,
                    running,
                    changedGoalLines,
                    changedGoalIds);
                continue;
            }

            var verificationGate = kernel.BuildVerificationGate(goal.Id);
            if (!verificationGate.IsSatisfied)
            {
                var blockingReasons = string.Join(
                    ',',
                    verificationGate.Tasks
                        .Where(task => task.GateStatus != VerificationGateStatus.Passed)
                        .Select(task => $"{task.Role}:{task.Reason}"));
                var reason = BoundSingleLine(
                    $"inconsistent {goal.Status} state: authoritative task verification gate unsatisfied ({blockingReasons}); " +
                    "apply verify-manual or retry before acceptance");
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=authoritative-verification-gate-unsatisfied goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
                continue;
            }

            var engineHealth = _acceptanceEngineCircuit?.Read();
            if (IsAcceptanceEngineCircuitHoldRequired(goal.Status, engineHealth))
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        BuildAcceptanceEngineHoldReason(engineHealth!)),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=held reason=acceptance-engine-circuit goal={goal.Id.Value[..8]} health={engineHealth.Health}",
                    changedGoalLines);
                continue;
            }

            if (retainedTerminal is null)
            {
                continue;
            }

            ReplayParallelAcceptanceLeaseReceipts(driver, retainedTerminal.Attempt, changedGoalLines);
            if (retainedTerminal.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed)
            {
                var run = retainedTerminal.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                    ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        retainedTerminal.Attempt.SlotIndex,
                        retainedTerminal.Attempt.ScopePaths ?? [],
                        retainedTerminal.Attempt.BranchHeadSha,
                        retainedTerminal.Attempt.MainHeadSha),
                    new InvalidOperationException("Completed acceptance attempt had no run result."));
                ReconcileParallelAcceptanceTerminalState(kernel, goal, run, retainedTerminal.Attempt);
                var result = CompleteParallelAcceptanceRun(
                    driver,
                    policy,
                    run,
                    retainedTerminal.Attempt,
                    out var evidenceMutationLeaseHeld);
                MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                    driver,
                    run,
                    evidenceMutationLeaseHeld,
                    retainedTerminal.Attempt);
                changedGoalIds.Add(goal.Id);
                results[goal.Id.Value] = new ParallelLandingOutcome(result, retainedTerminal.Attempt.SlotIndex);
                RecordParallelAcceptanceProgress(
                    AcceptanceLifecycleEventFormatter.Format(goal.Id.Value[..8], retainedTerminal.Attempt.SlotIndex, AcceptanceRunDisposition(run), retainedTerminal.Attempt.AttemptId, tick),
                    changedGoalLines);
                continue;
            }

            ReconcileParallelAcceptanceTerminalState(kernel, goal, retainedTerminal.Attempt);
            results[goal.Id.Value] = new ParallelLandingOutcome(
                ParallelAcceptanceTerminal(
                    driver,
                    ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        retainedTerminal.Attempt.SlotIndex,
                        retainedTerminal.Attempt.ScopePaths ?? [],
                        retainedTerminal.Attempt.BranchHeadSha,
                        retainedTerminal.Attempt.MainHeadSha),
                    policy,
                    retainedTerminal.Attempt),
                retainedTerminal.Attempt.SlotIndex);
            driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(retainedTerminal.Attempt);
            changedGoalIds.Add(goal.Id);
            RecordParallelAcceptanceProgress(
                AcceptanceLifecycleEventFormatter.Format(goal.Id.Value[..8], retainedTerminal.Attempt.SlotIndex, AcceptanceAttemptOutcomeToken(retainedTerminal.Attempt.Outcome), retainedTerminal.Attempt.AttemptId, tick),
                changedGoalLines);
            }
            catch (AcceptanceArtifactWriterLeaseBusyException ex)
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        $"acceptance artifact writer busy; retry on next conduct tick. {ex.Message}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=deferred reason=acceptance-artifact-writer-busy goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}",
                    changedGoalLines);
            }
            catch (Exception ex)
            {
                var reason = BoundSingleLine(
                    $"persisted parallel acceptance reconciliation fault isolated: {ex.GetType().Name}: {ex.Message}");
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=persisted-acceptance-reconciliation-fault goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
            }
        }
        var speculativeCandidates = orderedEligible
            .Select(goal => new ConductorSpeculativeAcceptanceCandidate(
                goal.Id,
                driver.ProjectGateReadyCandidate(goal, policy)))
            .ToArray();
        var activeCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
        var acceptanceCensus = CaptureLiveAcceptanceCensus(
            liveAttempts, activeAttemptIds, activeCohortCapacity, tick,
            changedGoalLines, blockAdmissionOnFailure: true);
        EmitSpeculativeCohortPlanReceipt(scopedGoals, verifiedGoalIdsAtTickStart,
            preWalkIntentChangedGoalIds, eligible, orderedEligible, speculativeCandidates,
            liveAttempts, activeCohortCapacity, acceptanceCensus, driver, policy,
            completedGoals, escalatedGoals, kernel, tick);
        var liveAttemptGoalIds = liveAttempts
            .Select(attempt => attempt.GoalId)
            .ToHashSet(StringComparer.Ordinal);
        var activeCohortMemberGoalIds = driver.GetActiveCohortGateMemberGoalIds((memberGoalIds, detail) =>
        {
            var memberIds = memberGoalIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            EmitProgress(
                $"ACCEPTANCE_COHORT_INFLIGHT tick={tick} goal={memberIds[0][..8]} " +
                $"members={string.Join(',', memberIds.Select(id => id[..8]))} {detail}");
        });
        var cohortEligible = orderedEligible
            .Where(goal => !liveAttemptGoalIds.Contains(goal.Id.Value) &&
                           !activeCohortMemberGoalIds.Contains(goal.Id.Value))
            .ToArray();
        var productionCandidates = ExcludeGroupedAcceptanceCandidatesWithNonAcceptanceObligations(
            speculativeCandidates, cohortEligible, liveAttemptGoalIds, activeCohortMemberGoalIds);
        var trainAdmission = DecideLiveAcceptanceAdmission(acceptanceCensus, configuredAcceptanceWidth);
        if (!suppressNewAcceptanceAdmission &&
            driver.AcceptanceCohortsEnabled &&
            cohortEligible.Length >= ConductorAcceptanceCohortSelector.CohortSize &&
            !cohortEligible.Any(goal => IsAcceptanceEngineCircuitHoldRequired(
                goal.Status, _acceptanceEngineCircuit?.Read())))
        {
            (cohortEligible, productionCandidates) = LandPassedAcceptanceCohortsBeforeTrainSelection(
                driver, policy, cohortEligible, productionCandidates, results, tick, changedGoalLines);
        }
        (cohortEligible, productionCandidates) = LandPassedMergeTrainReceiptsBeforeAdmission(
            driver, policy, cohortEligible, productionCandidates, results, tick, changedGoalLines);
        var groupedAdmissionOpen = !suppressNewAcceptanceAdmission &&
            trainAdmission.IsAdmitted &&
            driver.MergeTrainsEnabled &&
            cohortEligible.Length >= ConductorMergeTrainSelector.MinimumMembers &&
            !cohortEligible.Any(goal => IsAcceptanceEngineCircuitHoldRequired(
                goal.Status, _acceptanceEngineCircuit?.Read()));
        if (groupedAdmissionOpen &&
            cohortEligible.Length >= ConductorMergeTrainSelector.MinimumMembers &&
            ConductorMergeTrainSelector.Select(
                productionCandidates,
                driver.ReadSuppressedCohortPairs(),
                TrainIneligibleCriterionEvidenceGoalIds(cohortEligible)) is { } trainSelection)
        {
            var trainRun = driver.RunMergeTrain(
                trainSelection,
                cohortEligible,
                policy,
                onGateAdmitted: () => driver.RecordMergeTrainAdmissionFairness(trainSelection),
                runGateInBackground: true);
            foreach (var member in trainRun.MemberResults)
            {
                results[member.Key] = new ParallelLandingOutcome(member.Value, SlotIndex: 0);
            }
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_TRAIN tick={tick} members={string.Join(',', trainSelection.Members.Select(member => member.GoalId.Value[..8]))} " +
                $"ejected={string.Join(',', trainRun.Ejections.Select(ejection => ejection.GoalId.Value[..8]))} {trainRun.Detail}",
                changedGoalLines);
            cohortEligible = cohortEligible
                .Where(goal => !results.ContainsKey(goal.Id.Value))
                .ToArray();
            productionCandidates = productionCandidates
                .Where(candidate => !results.ContainsKey(candidate.GoalId.Value))
                .ToArray();
        }
        ConductorAcceptanceCohortFairnessPriority? forcedCohortPriority = null;
        var cohortAdmission = DecideLiveAcceptanceAdmission(acceptanceCensus, configuredAcceptanceWidth);
        if (!suppressNewAcceptanceAdmission &&
            cohortAdmission.IsAdmitted &&
            driver.AcceptanceCohortsEnabled &&
            cohortEligible.Length >= ConductorAcceptanceCohortSelector.CohortSize &&
            !cohortEligible.Any(goal => IsAcceptanceEngineCircuitHoldRequired(
                goal.Status,
                _acceptanceEngineCircuit?.Read())))
        {
            forcedCohortPriority = driver.SelectForcedCohortCandidate(cohortEligible);
            var cohortDecision = SelectAndReportAcceptanceCohort(productionCandidates, liveAttempts, forcedCohortPriority, driver);
            if (cohortDecision.Selection is { } cohortSelection)
            {
                var memberIds = string.Join(',', cohortSelection.Members.Select(member => member.GoalId.Value[..8]));
                var markerGoal = cohortSelection.Members[0].GoalId.Value[..8];
                EmitProgress(
                    $"ACCEPTANCE_COHORT_ENTRY tick={tick} goal={markerGoal} members={memberIds}");
                ConductorAcceptanceCohortRunResult cohortRun;
                ConductorAcceptanceCohortGateFault? gateFault;
                var exitOutcome = "exception";
                var exitReason = string.Empty;
                try
                {
                    var cohortOutcome = driver.RunAcceptanceCohortForTick(
                        cohortSelection,
                        cohortEligible,
                        policy,
                        onGateAdmitted: () => EmitAcceptanceCohortFairnessTransition(
                            driver.RecordCohortAdmissionFairness(cohortEligible, cohortSelection)),
                        runGateInBackground: true);
                    cohortRun = cohortOutcome.Run;
                    gateFault = cohortOutcome.Fault;
                    if (gateFault is { } observedFault)
                    {
                        var observedMemberIds = observedFault.MemberGoalIds
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray();
                        memberIds = string.Join(',', observedMemberIds.Select(id => id[..8]));
                        markerGoal = observedMemberIds[0][..8];
                    }
                    (exitOutcome, exitReason) = gateFault is { } backgroundFault
                        ? DescribeAcceptanceCohortGateFault(backgroundFault)
                        : DescribeAcceptanceCohortExit(cohortRun);
                }
                catch (Exception cohortGateException)
                {
                    // The cohort gate is the conductor's own machinery. Nothing it throws is allowed to end
                    // the tick: a transient fault is held and retried like the background attempt path does,
                    // and anything else escalates both members on the spot. Either way it leaves as data.
                    gateFault = ConductorDriver.CreateCohortGateFault(
                        cohortSelection,
                        cohortGateException);
                    cohortRun = new ConductorAcceptanceCohortRunResult(
                        Receipt: null,
                        new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
                        $"outcome=gate-fault fingerprint={gateFault.PairFingerprint} " +
                        $"fault={gateFault.FaultType} detail={SanitizeReason(gateFault.Message)}");
                    (exitOutcome, exitReason) = DescribeAcceptanceCohortGateFault(gateFault);
                }
                finally
                {
                    EmitProgress(
                        $"ACCEPTANCE_COHORT_EXIT tick={tick} goal={markerGoal} members={memberIds} outcome={exitOutcome}{exitReason}");
                }
                if (gateFault is { } cohortGateFault)
                {
                    cohortRun = ResolveFaultedAcceptanceCohort(
                        driver,
                        policy,
                        cohortEligible,
                        cohortRun,
                        cohortGateFault,
                        tick,
                        changedGoalLines);
                }
                foreach (var pair in cohortRun.MemberResults)
                {
                    results[pair.Key] = new ParallelLandingOutcome(pair.Value, SlotIndex: 0);
                }
                if (cohortRun.Detail.Contains("outcome=inflight", StringComparison.Ordinal))
                {
                    EmitProgress(
                        $"ACCEPTANCE_COHORT_INFLIGHT tick={tick} goal={markerGoal} members={memberIds} {cohortRun.Detail}");
                }
                RecordParallelAcceptanceProgress(
                    $"ACCEPTANCE_COHORT tick={tick} members={string.Join(',', cohortSelection.Members.Select(member => member.GoalId.Value[..8]))} {cohortRun.Detail}",
                    changedGoalLines);
                activeCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
                acceptanceCensus = CaptureLiveAcceptanceCensus(
                    liveAttempts,
                    activeAttemptIds,
                    activeCohortCapacity,
                    tick,
                    changedGoalLines,
                    blockAdmissionOnFailure: true);
            }
            else if (cohortDecision.Exclusions.Count > 0)
            {
                RecordParallelAcceptanceProgress(
                    $"ACCEPTANCE_COHORT tick={tick} outcome=unpaired exclusions={FormatCohortPairExclusions(cohortDecision.Exclusions)} fallback=ordinary",
                    changedGoalLines);
            }
        }
        foreach (var goal in orderedEligible)
        {
            if (results.ContainsKey(goal.Id.Value) ||
                !driver.TryGetCohortGateHold(goal.Id, out var cohortHoldDetail))
            {
                continue;
            }

            results[goal.Id.Value] = new ParallelLandingOutcome(
                ParallelAcceptanceHeld(
                    goal,
                    policy,
                    $"Acceptance cohort gate owns this member: {cohortHoldDetail}"),
                SlotIndex: null);
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_COHORT tick={tick} goal={goal.Id.Value[..8]} result=held {cohortHoldDetail}",
                changedGoalLines);
        }
        if (suppressNewAcceptanceAdmission) return results;
        HoldLoneReadyGoalForInReviewCohortPartner(kernel, scopedGoals, orderedEligible, productionCandidates,
            acceptanceCensus, driver, policy, results, tick, changedGoalLines);
        var oldestWaiterObservation = ObserveOldestParallelAcceptanceWaiter(orderedEligible, liveAttemptGoalIds);
        var oldestWaiter = oldestWaiterObservation.Waiter;
        var oldestServedThisTick = false;
        foreach (var goal in orderedEligible)
        {
            if (results.ContainsKey(goal.Id.Value))
            {
                continue;
            }
            try
            {
            if (goal.Status == GoalStatus.Completed)
            {
                const string reason = "Completed goal requires operator acceptance; background acceptance cannot reopen a landed goal.";
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=held reason=completed-goal-operator-acceptance goal={goal.Id.Value[..8]}",
                    changedGoalLines);
                continue;
            }

            var verificationGate = kernel.BuildVerificationGate(goal.Id);
            if (!verificationGate.IsSatisfied)
            {
                var blockingReasons = string.Join(
                    ',',
                    verificationGate.Tasks
                        .Where(task => task.GateStatus != VerificationGateStatus.Passed)
                        .Select(task => $"{task.Role}:{task.Reason}"));
                var reason = BoundSingleLine(
                    $"inconsistent {goal.Status} state: authoritative task verification gate unsatisfied ({blockingReasons}); " +
                    "apply verify-manual or retry before acceptance");
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=authoritative-verification-gate-unsatisfied goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
                continue;
            }

            var engineHealth = _acceptanceEngineCircuit?.Read();
            if (IsAcceptanceEngineCircuitHoldRequired(goal.Status, engineHealth))
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        BuildAcceptanceEngineHoldReason(engineHealth!)),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=held reason=acceptance-engine-circuit goal={goal.Id.Value[..8]} health={engineHealth.Health}",
                    changedGoalLines);
                continue;
            }

            int acceptanceSlotCount;
            try
            {
                acceptanceSlotCount = driver.GetAcceptanceSlotCount(goal);
                if (acceptanceSlotCount is < 1 || acceptanceSlotCount > MaxParallelAcceptanceCapacity)
                {
                    throw new InvalidDataException(
                        $"Acceptance slot count {acceptanceSlotCount} must be between 1 and maximum {MaxParallelAcceptanceCapacity}.");
                }
                acceptanceSlotCount = Math.Min(acceptanceSlotCount, configuredAcceptanceWidth);
            }
            catch (Exception ex)
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    driver.EscalateParallelLandingAcceptance(
                        goal,
                        policy,
                        $"invalid parallel acceptance slot settings: {SanitizeReason(ex.Message)}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=parallel-acceptance-slot-settings goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}",
                    changedGoalLines);
                continue;
            }

            var ordinaryAdmission = DecideLiveAcceptanceAdmission(acceptanceCensus, acceptanceSlotCount);
            if (!ordinaryAdmission.IsAdmitted)
            {
                deferredByAdmission++;
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        ordinaryAdmission.Reason),
                    null);
                continue;
            }

            ParallelAcceptanceOldestWaiterObservation? stalledOldestBypass = null;
            if (!liveAttemptGoalIds.Contains(goal.Id.Value) &&
                oldestWaiter is not null &&
                goal.Id != oldestWaiter.Id &&
                !oldestServedThisTick &&
                ShouldDeferForParallelAcceptanceFairness(oldestWaiter.Id.Value))
            {
                if (oldestWaiterObservation.IsStalled)
                {
                    stalledOldestBypass = oldestWaiterObservation;
                }
                else
                {
                    var deferredCandidate = TryBuildParallelAcceptanceCandidate(
                        driver,
                        goal,
                        policy,
                        SelectAvailableParallelAcceptanceSlot(
                            activeAttemptSlotIndexes,
                            acceptanceSlotCount),
                        out var deferredBuildException);
                    if (deferredCandidate is not null)
                    {
                        results[goal.Id.Value] = new ParallelLandingOutcome(
                            ParallelAcceptanceHeld(
                                deferredCandidate,
                                policy,
                                $"parallel acceptance fairness waiting for oldest verified goal {oldestWaiter.Id.Value[..8]}; retry on next conduct tick"),
                            null);
                        RecordParallelAcceptanceProgress(
                            $"ADMISSION tick={tick} result=deferred reason=parallel-acceptance-fairness goal={goal.Id.Value[..8]} oldest={oldestWaiter.Id.Value[..8]}",
                            changedGoalLines);
                    }
                    else if (deferredBuildException is not null)
                    {
                        var unavailableReason = FormatParallelAcceptanceCandidateUnavailable(deferredBuildException);
                        results[goal.Id.Value] = new ParallelLandingOutcome(
                            ParallelAcceptanceHeld(
                                goal,
                                policy,
                                unavailableReason),
                            null);
                        RecordParallelAcceptanceProgress(
                            $"ADMISSION tick={tick} result=held reason=parallel-acceptance-candidate goal={goal.Id.Value[..8]} detail={FormatParallelAcceptanceCandidateUnavailableDetail(deferredBuildException)}",
                            changedGoalLines);
                    }

                    continue;
                }
            }

            var candidate = TryBuildParallelAcceptanceCandidate(
                driver,
                goal,
                policy,
                SelectAvailableParallelAcceptanceSlot(
                    activeAttemptSlotIndexes,
                    acceptanceSlotCount),
                out var buildException);
            if (candidate is null)
            {
                if (buildException is not null)
                {
                    var unavailableReason = FormatParallelAcceptanceCandidateUnavailable(buildException);
                    results[goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            goal,
                            policy,
                            unavailableReason),
                        null);
                    RecordParallelAcceptanceProgress(
                        $"ADMISSION tick={tick} result=held reason=parallel-acceptance-candidate goal={goal.Id.Value[..8]} detail={FormatParallelAcceptanceCandidateUnavailableDetail(buildException)}",
                        changedGoalLines);
                }

                continue;
            }

            if (activeCandidates.Any(existing => existing.Overlaps(candidate)))
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        candidate,
                        policy,
                        "parallel acceptance resource conflict; retry on next conduct tick"),
                    null);
                continue;
            }

            var documentationExclusionAdmission = BuildDocumentationExclusionAdmissionRecord(
                candidate,
                activeCandidates,
                tick);
            var decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                driver.RunParallelLandingAcceptance);
            if (forcedCohortPriority?.GoalId == goal.Id &&
                decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
                    ConductorParallelAcceptanceAttemptDecisionKind.Running or
                    ConductorParallelAcceptanceAttemptDecisionKind.Completed)
            {
                driver.ResetCohortFairness(goal.Id);
            }
            ReplayParallelAcceptanceLeaseReceipts(driver, decision.Attempt, changedGoalLines);

            switch (decision.Kind)
            {
                case ConductorParallelAcceptanceAttemptDecisionKind.Started:
                    if (documentationExclusionAdmission is not null)
                    {
                        RecordParallelAcceptanceProgress(documentationExclusionAdmission, changedGoalLines);
                    }

                    if (decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
                    {
                        var terminalDecision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                            candidate,
                            policy,
                            driver.RunParallelLandingAcceptance);
                        ReplayParallelAcceptanceLeaseReceipts(driver, terminalDecision.Attempt, changedGoalLines);
                        if (terminalDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed)
                        {
                            var terminalRun = terminalDecision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                                candidate,
                                new InvalidOperationException("Completed acceptance attempt had no run result."));
                            ReconcileParallelAcceptanceTerminalState(kernel, goal, terminalRun, terminalDecision.Attempt);
                            var terminalResult = CompleteParallelAcceptanceRun(
                                driver,
                                policy,
                                terminalRun,
                                terminalDecision.Attempt,
                                out var terminalEvidenceMutationLeaseHeld);
                            MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                                driver,
                                terminalRun,
                                terminalEvidenceMutationLeaseHeld,
                                terminalDecision.Attempt);
                            oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                            RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                            RecordParallelAcceptanceFairnessAdmission(
                                goal,
                                oldestWaiter,
                                stalledOldestBypass,
                                tick,
                                changedGoalLines);
                            results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(terminalResult, candidate.SlotIndex);
                            RecordParallelAcceptanceProgress(
                                AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceRunDisposition(terminalRun), terminalDecision.Attempt.AttemptId, tick),
                                changedGoalLines);
                            break;
                        }

                        ReconcileParallelAcceptanceTerminalState(kernel, goal, terminalDecision.Attempt);
                        results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                            ParallelAcceptanceTerminal(driver, candidate, policy, terminalDecision.Attempt),
                            candidate.SlotIndex);
                        driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(terminalDecision.Attempt);
                        RecordParallelAcceptanceProgress(
                            AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceAttemptOutcomeToken(terminalDecision.Attempt.Outcome), terminalDecision.Attempt.AttemptId, tick),
                            changedGoalLines);
                        break;
                    }

                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    ReserveParallelAcceptanceCandidate(
                        candidate,
                        decision.Attempt,
                        liveAttempts,
                        activeCandidates,
                        activeAttemptIds,
                        activeAttemptSlotIndexes);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification running in background"),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, "started", decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Running:
                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    ReserveParallelAcceptanceCandidate(
                        candidate,
                        decision.Attempt,
                        liveAttempts,
                        activeCandidates,
                        activeAttemptIds,
                        activeAttemptSlotIndexes);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification still running in background"),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, "running", decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Completed:
                    var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                        candidate,
                        new InvalidOperationException("Completed acceptance attempt had no run result."));
                    ReconcileParallelAcceptanceTerminalStateUnlessReused(kernel, goal, run, decision.Attempt);
                    var result = CompleteParallelAcceptanceRun(
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
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(result, candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceRunDisposition(run), decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun:
                    ReconcileParallelAcceptanceTerminalState(kernel, goal, decision.Attempt);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceTerminal(driver, candidate, policy, decision.Attempt),
                        candidate.SlotIndex);
                    driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome), decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
            }
            acceptanceCensus = CaptureLiveAcceptanceCensus(
                liveAttempts,
                activeAttemptIds,
                activeCohortCapacity,
                tick,
                changedGoalLines,
                blockAdmissionOnFailure: true);
            }
            catch (AcceptanceArtifactWriterLeaseBusyException ex)
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        $"acceptance artifact writer busy; retry on next conduct tick. {ex.Message}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=deferred reason=acceptance-artifact-writer-busy goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}",
                    changedGoalLines);
            }
            catch (Exception ex)
            {
                var reason = BoundSingleLine(
                    $"parallel acceptance fault isolated before goal advance: {ex.GetType().Name}: {ex.Message}");
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=parallel-acceptance-fault goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
            }
        }

        if (deferredByAdmission > 0)
        {
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=deferred reason=parallel-acceptance-slot-cap cap={configuredAcceptanceWidth} deferred={deferredByAdmission}",
                changedGoalLines);
        }

        return results;
    }

    internal static void ReconcileParallelAcceptanceTerminalState(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        EnsureParallelAcceptanceTerminalIsVerifying(kernel, goal, attempt);
        if (ReconcileIdentityStaleAcceptance(kernel, goal, run, attempt))
        {
            return;
        }
        if (goal.Status != GoalStatus.Verifying)
        {
            return;
        }

        var disposition = AcceptanceRunDisposition(run);
        if (IsPassingAcceptanceRun(run))
        {
            kernel.ReconcileGoalAcceptanceVerified(
                goal.Id,
                $"Batch loop reconciled background acceptance gate {attempt.AttemptId} terminal artifact ({disposition}); goal returned to Verified for deterministic landing classification.");
            return;
        }

        if (IsEnvironmentInterferenceAcceptanceRun(run))
        {
            kernel.ReconcileGoalAcceptanceVerified(
                goal.Id,
                $"Batch loop reconciled background acceptance gate {attempt.AttemptId} environmental interference ({disposition}); goal returned to Verified for re-gating.");
            return;
        }

        if (IsRetryableAcceptanceRun(run))
        {
            return;
        }

        if (run.Acceptance is null && ConductorParallelAcceptanceAttemptCoordinator
                .ClassifyWorkerRegistrationFault(run.Exception) != WorkerRegistrationFaultDisposition.None)
        {
            // Keep classified registration faults distinct; unclassified faults retain AcceptanceFailed.
            return;
        }

        kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            BuildFailedAcceptanceChecks(run, attempt),
            $"Batch loop reconciled background acceptance gate {attempt.AttemptId} terminal artifact ({disposition}); goal moved to AcceptanceFailed.",
            run.Candidate.BranchHeadSha ?? attempt.BranchHeadSha,
            run.Candidate.MainHeadSha ?? attempt.MainHeadSha,
            run.Acceptance?.CheckAttributions,
            run.Acceptance?.BaselineAttestation);
    }

    internal static void ReconcileParallelAcceptanceTerminalState(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttempt attempt)
    {
        EnsureParallelAcceptanceTerminalIsVerifying(kernel, goal, attempt);
        kernel.ResetAcceptanceIdentityStale(goal.Id);
        if (goal.Status != GoalStatus.Verifying)
        {
            return;
        }

        if (IsRetryableTerminalAttempt(attempt))
        {
            return;
        }

        // Terminal-without-run outcomes are process/artifact infrastructure failures, not positive
        // acceptance-test failures. The caller surfaces the over-budget case as an operator escalation.
        return;
    }

    private static bool IsPassingAcceptanceRun(ConductorParallelAcceptanceRunResult run) =>
        run.Exception is null &&
        (run.EarlyResult is not null
            ? !run.EarlyResult.WasEscalated
            : run.Acceptance is { Passed: true });

    private static bool IsRetryableAcceptanceRun(ConductorParallelAcceptanceRunResult run) =>
        run.EarlyResult is not null ||
        run.Exception is AcceptanceInfrastructureDeferredException or
            AcceptanceGateEngineException or
            DotnetBuildSlotsBusyException or
            BuildLockBlockedException or
            OperationCanceledException;

    private static bool IsEnvironmentInterferenceAcceptanceRun(ConductorParallelAcceptanceRunResult run) =>
        run.Acceptance is { } acceptance &&
        ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(acceptance);

    private static bool IsRetryableTerminalAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.Outcome is ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot
            or ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock
            or ConductorParallelAcceptanceAttemptOutcome.Cancelled ||
        ConductorParallelAcceptanceAttemptCoordinator.IsTransientTerminalFailure(attempt) &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap;

    private static IReadOnlyList<string> BuildFailedAcceptanceChecks(
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (run.Acceptance is { } acceptance)
        {
            var checks = acceptance.FailedChecks is { Count: > 0 }
                ? acceptance.FailedChecks
                : acceptance.RequiredUnmetCriteria.Select(criterion => criterion.Name).ToArray();
            if (checks.Count > 0)
            {
                return checks;
            }
        }

        if (run.Exception is not null)
        {
            return [$"background-acceptance-fault: {SanitizeReason(run.Exception.Message)}"];
        }

        if (run.EarlyOutcome is not null)
        {
            return [$"{run.EarlyOutcome.Kind}: {SanitizeReason(run.EarlyOutcome.Detail)}"];
        }

        return [AcceptanceAttemptFailureCheck(attempt)];
    }

    private static string AcceptanceAttemptFailureCheck(ConductorParallelAcceptanceAttempt attempt) =>
        $"background-acceptance-{AcceptanceAttemptOutcomeToken(attempt.Outcome)}: {SanitizeReason(attempt.Detail ?? attempt.AttemptId)}";

    private static void EnsureParallelAcceptanceTerminalIsVerifying(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (goal.Status == GoalStatus.Verified)
        {
            kernel.BeginGoalAcceptanceVerification(
                goal.Id,
                $"Batch loop observed terminal background acceptance gate {attempt.AttemptId}; goal entered Verifying before terminal reconciliation.");
        }
    }

    internal static Goal? SelectOldestParallelAcceptanceWaiter(
        IReadOnlyList<Goal> orderedEligible,
        IReadOnlySet<string> liveAttemptGoalIds) =>
        orderedEligible.FirstOrDefault(goal => !liveAttemptGoalIds.Contains(goal.Id.Value));

    internal static ParallelAcceptanceOldestWaiterObservation ObserveOldestParallelAcceptanceWaiter(
        IReadOnlyList<Goal> orderedEligible,
        IReadOnlySet<string> liveAttemptGoalIds)
    {
        var waiter = SelectOldestParallelAcceptanceWaiter(orderedEligible, liveAttemptGoalIds);
        lock (ParallelAcceptanceFairnessGate)
        {
            if (waiter is null)
            {
                s_parallelAcceptanceObservedOldestWaiter = null;
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks = 0;
                return new ParallelAcceptanceOldestWaiterObservation(null, 0);
            }

            if (!string.Equals(
                    s_parallelAcceptanceObservedOldestWaiter,
                    waiter.Id.Value,
                    StringComparison.Ordinal))
            {
                s_parallelAcceptanceObservedOldestWaiter = waiter.Id.Value;
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks = 1;
            }
            else if (s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks < int.MaxValue)
            {
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks++;
            }

            return new ParallelAcceptanceOldestWaiterObservation(
                waiter,
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks);
        }
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

    internal static bool ShouldDeferForParallelAcceptanceFairness(string oldestGoalId)
    {
        lock (ParallelAcceptanceFairnessGate)
        {
            if (!string.Equals(s_parallelAcceptanceOldestWaiter, oldestGoalId, StringComparison.Ordinal))
            {
                s_parallelAcceptanceOldestWaiter = oldestGoalId;
                s_parallelAcceptanceConsecutiveOvertakes = 0;
                return false;
            }

            return s_parallelAcceptanceConsecutiveOvertakes >= ParallelAcceptanceBoundedOvertakeLimit;
        }
    }

    internal static void ResetParallelAcceptanceFairnessForTests()
    {
        lock (ParallelAcceptanceFairnessGate)
        {
            s_parallelAcceptanceOldestWaiter = null;
            s_parallelAcceptanceConsecutiveOvertakes = 0;
            s_parallelAcceptanceObservedOldestWaiter = null;
            s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks = 0;
        }
    }

    private static void RecordParallelAcceptanceFairnessGrant(string goalId, string? oldestGoalId)
    {
        if (string.IsNullOrWhiteSpace(oldestGoalId))
        {
            return;
        }

        lock (ParallelAcceptanceFairnessGate)
        {
            if (string.Equals(goalId, oldestGoalId, StringComparison.Ordinal))
            {
                s_parallelAcceptanceOldestWaiter = oldestGoalId;
                s_parallelAcceptanceConsecutiveOvertakes = 0;
                return;
            }

            if (!string.Equals(s_parallelAcceptanceOldestWaiter, oldestGoalId, StringComparison.Ordinal))
            {
                s_parallelAcceptanceOldestWaiter = oldestGoalId;
                s_parallelAcceptanceConsecutiveOvertakes = 0;
            }

            s_parallelAcceptanceConsecutiveOvertakes++;
        }
    }

    private static void RecordParallelAcceptanceFairnessCapIfReached(
        Goal goal,
        Goal? oldestWaiter,
        int tick,
        List<string> changedGoalLines)
    {
        if (oldestWaiter is null ||
            goal.Id == oldestWaiter.Id ||
            !IsParallelAcceptanceFairnessAtLimit(oldestWaiter.Id.Value))
        {
            return;
        }

        RecordParallelAcceptanceProgress(
            $"ADMISSION tick={tick} result=cap-reached reason=parallel-acceptance-fairness goal={goal.Id.Value[..8]} oldest={oldestWaiter.Id.Value[..8]} limit={ParallelAcceptanceBoundedOvertakeLimit}",
            changedGoalLines);
    }

    private static void RecordParallelAcceptanceFairnessAdmission(
        Goal goal,
        Goal? oldestWaiter,
        ParallelAcceptanceOldestWaiterObservation? stalledOldestBypass,
        int tick,
        List<string> changedGoalLines)
    {
        if (stalledOldestBypass is { } bypass && bypass.Waiter is not null)
        {
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=admitted reason=parallel-acceptance-stalled-oldest-bypass goal={goal.Id.Value[..8]} bypassedOldest={bypass.Waiter.Id.Value[..8]} stalledTicks={bypass.ConsecutiveTicks} threshold={ParallelAcceptanceOldestWaiterStallTickThreshold}",
                changedGoalLines);
            return;
        }

        RecordParallelAcceptanceFairnessCapIfReached(goal, oldestWaiter, tick, changedGoalLines);
    }

    private static bool IsParallelAcceptanceFairnessAtLimit(string oldestGoalId)
    {
        lock (ParallelAcceptanceFairnessGate)
        {
            return string.Equals(s_parallelAcceptanceOldestWaiter, oldestGoalId, StringComparison.Ordinal) &&
                s_parallelAcceptanceConsecutiveOvertakes >= ParallelAcceptanceBoundedOvertakeLimit;
        }
    }

    private static void RecordParallelAcceptanceProgress(string line, List<string> changedGoalLines) =>
        changedGoalLines.Add(line);

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
        var candidate = TryBuildParallelAcceptanceCandidate(driver, goal, policy, slotIndex: 0, out var buildException);
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

    internal static ConductorAdvanceResult CompleteParallelAcceptanceRun(
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt,
        out bool evidenceMutationLeaseHeld)
    {
        evidenceMutationLeaseHeld = false;
        if (run.Exception is not null)
        {
            if (IsIdentityStaleRun(run))
            {
                return CompleteIdentityStaleRun(driver, policy, run, attempt);
            }

            if (run.Exception is AcceptanceGateEngineException gateEngineFault)
            {
                if (attempt.TransientFailureCount >= ParallelAcceptanceTransientFailureCap)
                {
                    return driver.EscalateParallelLandingAcceptance(
                        run.Candidate,
                        policy,
                        $"background acceptance gate-engine fault: {SanitizeReason(gateEngineFault.Message)}",
                        ConductorEscalationKind.BackgroundAcceptanceFailed);
                }

                return ParallelAcceptanceHeld(
                    run.Candidate,
                    policy,
                    $"Acceptance gate engine fault ({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); " +
                    $"retry on next conduct tick. {gateEngineFault.Message}");
            }

            if (run.Exception is AcceptanceInfrastructureDeferredException infrastructureDeferred)
            {
                if (attempt.TransientFailureCount >= ParallelAcceptanceTransientFailureCap)
                {
                    return driver.EscalateParallelLandingAcceptance(
                        run.Candidate,
                        policy,
                        $"background acceptance infrastructure-deferred: {SanitizeReason(infrastructureDeferred.Message)}",
                        ConductorEscalationKind.BackgroundAcceptanceFailed);
                }

                return ParallelAcceptanceHeld(
                    run.Candidate,
                    policy,
                    $"Acceptance infrastructure deferred ({infrastructureDeferred.ReasonCode}) " +
                    $"({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); retry on next conduct tick. " +
                    infrastructureDeferred.Message);
            }

            if (run.Exception is DotnetBuildSlotsBusyException slotsBusy)
            {
                return new ConductorAdvanceResult(
                    run.Candidate.Goal.Id.Value,
                    run.Candidate.GoalPrefix,
                    policy.Name,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Stable dotnet build slots busy; retry on next conduct tick. {FormatSlotsBusy(slotsBusy.SlotsBusy)}"));
            }

            if (run.Exception is OperationCanceledException cancelled)
            {
                return ParallelAcceptanceHeld(
                    run.Candidate,
                    policy,
                    $"Background acceptance attempt cancelled; retry on next conduct tick: {SanitizeReason(cancelled.Message)}");
            }

            if (run.Exception is BuildLockBlockedException buildLock)
            {
                if (attempt.TransientFailureCount >= ParallelAcceptanceTransientFailureCap)
                {
                    return driver.EscalateParallelLandingAcceptance(
                        run.Candidate,
                        policy,
                        $"background acceptance blocked-build-lock: {FormatBuildLockBlocked(buildLock.Attribution)}",
                        ConductorEscalationKind.BackgroundAcceptanceFailed);
                }

                return new ConductorAdvanceResult(
                    run.Candidate.Goal.Id.Value,
                    run.Candidate.GoalPrefix,
                    policy.Name,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Build artifact lock blocked acceptance; retry on next conduct tick. {FormatBuildLockBlocked(buildLock.Attribution)}"));
            }

            return ParallelAcceptanceFault(driver, run.Candidate, policy, attempt, run.Exception);
        }

        if (run.EarlyResult is not null)
        {
            return driver.ReplayParallelLandingEarlyOutcome(run.Candidate, policy, run.EarlyResult, run.EarlyOutcome);
        }

        if (run.Acceptance is null)
        {
            return ParallelAcceptanceUnclassifiedFault(
                driver,
                run.Candidate,
                policy,
                new InvalidOperationException("Parallel acceptance produced no result."));
        }

        try
        {
            return driver.CompleteParallelLandingAcceptance(
                run.Candidate,
                policy,
                run.Acceptance,
                out evidenceMutationLeaseHeld);
        }
        catch (Exception ex)
        {
            return ParallelAcceptanceUnclassifiedFault(driver, run.Candidate, policy, ex);
        }
    }

    internal static void MarkParallelAcceptanceReconciledUnlessLeaseHeld(
        ConductorDriver driver,
        ConductorParallelAcceptanceRunResult run,
        bool evidenceMutationLeaseHeld,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (run.Acceptance is { Passed: true } && evidenceMutationLeaseHeld)
        {
            return;
        }

        driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(attempt);
    }

    private static ConductorAdvanceResult ParallelAcceptanceHeld(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string reason) =>
        ParallelAcceptanceHeld(candidate.Goal, policy, reason);

    private static ConductorAdvanceResult ParallelAcceptanceHeld(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string reason) =>
        new(
            goal.Id.Value,
            goal.Id.Value[..8],
            policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason));

    private static string BuildAcceptanceEngineHoldReason(AcceptanceEngineHealthSnapshot snapshot)
    {
        var decision = AcceptanceEngineAcceptanceGate.Decide(
            snapshot.Health,
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
        return $"{decision.Reason}" +
               (string.IsNullOrWhiteSpace(snapshot.LandingSha) ? string.Empty : $"; landing={snapshot.LandingSha}") +
               (string.IsNullOrWhiteSpace(snapshot.FailureReason) ? string.Empty : $"; failure={snapshot.FailureReason}") +
               "; acceptance and landing are blocked until the canary passes or an operator runs acceptance-engine clear.";
    }

    internal static bool IsAcceptanceEngineCircuitHoldRequired(
        GoalStatus goalStatus,
        AcceptanceEngineHealthSnapshot? snapshot) =>
        goalStatus == GoalStatus.Verified &&
        snapshot is not null &&
        !AcceptanceEngineAcceptanceGate.Decide(
            snapshot.Health,
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy).Allowed;

    internal static ConductorAdvanceResult ParallelAcceptanceTerminal(
        ConductorDriver driver,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Stable dotnet build slots busy in background acceptance attempt; retry on next conduct tick. attempt={attempt.AttemptId}");
        }

        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Build artifact lock blocked background acceptance attempt; retry on next conduct tick. attempt={attempt.AttemptId}");
        }

        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Cancelled)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Background acceptance attempt cancelled; retry on next conduct tick. attempt={attempt.AttemptId}: {SanitizeReason(attempt.Detail ?? "cancelled")}");
        }

        if (ConductorParallelAcceptanceAttemptCoordinator.IsTransientTerminalFailure(attempt) &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Transient background acceptance {AcceptanceAttemptOutcomeToken(attempt.Outcome)} ({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); retry on next conduct tick. attempt={attempt.AttemptId}: {SanitizeReason(attempt.Detail ?? "transient artifact fault")}");
        }

        return driver.EscalateParallelLandingAcceptance(
            candidate,
            policy,
            $"background acceptance {AcceptanceAttemptOutcomeToken(attempt.Outcome)}: {SanitizeReason(attempt.Detail ?? attempt.AttemptId)}",
            ConductorEscalationKind.BackgroundAcceptanceFailed);
    }

    private static ConductorAdvanceResult ParallelAcceptanceFault(
        ConductorDriver driver,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceAttempt attempt,
        Exception exception)
    {
        var registrationFault =
            ConductorParallelAcceptanceAttemptCoordinator.WorkerRegistrationFaultMessage(exception);
        var disposition =
            ConductorParallelAcceptanceAttemptCoordinator.ClassifyWorkerRegistrationFault(registrationFault);
        if (disposition == WorkerRegistrationFaultDisposition.BoundedRetry &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Transient worker-process registration fault ({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); retry on next conduct tick. attempt={attempt.AttemptId}: {registrationFault}");
        }

        if (disposition is WorkerRegistrationFaultDisposition.BoundedRetry or WorkerRegistrationFaultDisposition.Terminal)
        {
            return driver.EscalateParallelLandingAcceptance(
                candidate,
                policy,
                $"background acceptance worker-process registration fault: {registrationFault}",
                ConductorEscalationKind.BackgroundAcceptanceFailed);
        }

        return ParallelAcceptanceUnclassifiedFault(driver, candidate, policy, exception);
    }

    private static ConductorAdvanceResult ParallelAcceptanceUnclassifiedFault(
        ConductorDriver driver,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Exception exception) =>
        driver.EscalateParallelLandingAcceptance(
            candidate,
            policy,
            $"parallel acceptance fault: {SanitizeReason(exception.Message)}");

    internal static string AcceptanceRunDisposition(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return run.Exception switch
            {
                DotnetBuildSlotsBusyException => "slots-busy",
                OperationCanceledException => "cancelled",
                BuildLockBlockedException => "build-lock-blocked",
                AcceptanceInfrastructureDeferredException deferred when
                    deferred.ReasonCode == "structural-coverage-permit-unavailable" =>
                    "structural-coverage-permit-unavailable",
                AcceptanceInfrastructureDeferredException => "infrastructure-deferred",
                AcceptanceGateEngineException => "gate-engine-fault",
                AcceptanceExecutionIdentityChangedException { IsChangedIdentity: true } => IdentityStaleDisposition,
                _ => "fault"
            };
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.WasEscalated ? "blocked" : "done";
        }

        return run.Acceptance?.Passed == true ? "passed" : "failed";
    }

    internal static string AcceptanceAttemptOutcomeToken(ConductorParallelAcceptanceAttemptOutcome outcome) =>
        outcome switch
        {
            ConductorParallelAcceptanceAttemptOutcome.Running => "running",
            ConductorParallelAcceptanceAttemptOutcome.Passed => "passed",
            ConductorParallelAcceptanceAttemptOutcome.Failed => "failed",
            ConductorParallelAcceptanceAttemptOutcome.StaleCandidate => "stale-candidate",
            ConductorParallelAcceptanceAttemptOutcome.ProcessDied => "process-died",
            ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts => "corrupt-artifacts",
            ConductorParallelAcceptanceAttemptOutcome.Cancelled => "cancelled",
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot => "blocked-build-slot",
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock => "blocked-build-lock",
            ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred => "infrastructure-deferred",
            ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable => "structural-coverage-permit-unavailable",
            ConductorParallelAcceptanceAttemptOutcome.GateEngineFault => "gate-engine-fault",
            ConductorParallelAcceptanceAttemptOutcome.LaunchFailed => "launch-failed",
            ConductorParallelAcceptanceAttemptOutcome.Faulted => "faulted",
            ConductorParallelAcceptanceAttemptOutcome.Reconciled => "reconciled",
            _ => "unknown"
        };

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

    private static string FormatGoalProgressLine(string label, ConductorAdvanceOutcome outcome, int? slotIndex = null)
    {
        var slot = slotIndex.HasValue ? $" slot=slot-{slotIndex.Value}" : string.Empty;
        return outcome switch
        {
            ConductorAdvanceOutcome.Executed e  => $"GOAL goal={label} result=executed state={e.FromState}{slot}",
            ConductorAdvanceOutcome.Held h      => $"GOAL goal={label} result=held state={h.State}{slot} reason={SanitizeReason(h.Reason)}",
            ConductorAdvanceOutcome.Escalated e => $"GOAL goal={label} result=escalated state={e.State}{slot} reason={SanitizeReason(e.Reason)}",
            ConductorAdvanceOutcome.Done d      => $"GOAL goal={label} result=done state={d.State}{slot}",
            _                                   => $"GOAL goal={label} result=unknown{slot}"
        };
    }


    private static string? GetDependencyHoldReason(
        Goal goal,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel)
    {
        foreach (var depId in goal.DependsOn)
        {
            if (completedGoals.Contains(depId.Value) ||
                kernel.IsKnownCompletedDependencyGoal(depId) ||
                (kernel.TryGetKnownDependencyGoalStatus(depId, out var dependencyStatus) &&
                 IsMetadataSatisfiedDependencyStatus(dependencyStatus)))
            {
                continue;
            }

            if (kernel.TryGetKnownDependencyGoalStatus(depId, out var terminalStatus) &&
                IsTerminalWithoutLandingDependencyStatus(terminalStatus))
            {
                return $"dependency-terminal-without-landing: {depId.Value[..8]} state={terminalStatus}";
            }

            if (escalatedGoals.Contains(depId.Value))
                return $"dependency escalated: {depId.Value[..8]}";

            if (kernel.TryGetKnownDependencyGoalStatus(depId, out var knownStatus) &&
                knownStatus.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return $"waiting on dependency {depId.Value[..8]}";
            }

            var depPrefix = kernel.Goals.FirstOrDefault(g => g.Id == depId)?.Id.Value[..8] ?? depId.Value[..8];
            return $"waiting on dependency {depPrefix}";
        }

        return null;
    }

    private static bool HasStartedGoalWork(Goal goal) =>
        goal.Tasks.Any(task =>
            task.Status == WorkTaskStatus.Running ||
            task.LastProcess is { IsRunning: true });

    private static bool IsMetadataSatisfiedDependencyStatus(string status) =>
        status.Equals("CleanedUp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalWithoutLandingDependencyStatus(string status) =>
        status.Equals(GoalStatus.Failed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Superseded.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Retired", StringComparison.OrdinalIgnoreCase);

    private static void MarkCompletedDependencyGoals(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        string? onlyGoalId,
        HashSet<string> completedGoals,
        GoalProjectionCache goalProjectionCache)
    {
        foreach (var goal in kernel.Goals)
        {
            if (onlyGoalId is not null && goal.Id.Value != onlyGoalId)
            {
                continue;
            }

            if (completedGoals.Contains(goal.Id.Value))
            {
                continue;
            }

            if (IsPreWalkExcludedGoal(goal) || goal.Status is not (GoalStatus.Verifying or GoalStatus.Verified or GoalStatus.Completed))
            {
                continue;
            }

            GoalLifecycleState state;
            try
            {
                state = goalProjectionCache.ResolveState(goal, driver);
            }
            catch
            {
                continue;
            }

            if (state is not (GoalLifecycleState.Merged or GoalLifecycleState.Recorded or GoalLifecycleState.CleanedUp))
            {
                continue;
            }

            completedGoals.Add(goal.Id.Value);
            kernel.MarkKnownCompletedDependencyGoals([goal.Id]);
        }
    }

    private static void ReadmitResolvedSetAsideGoals(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        TerminalGoalSweepResult? sweepResult,
        string? onlyGoalId,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        Dictionary<string, BatchSetAsideEntry> selfClearedSetAsideEntries,
        HashSet<string> excludedGoals,
        HashSet<string> escalatedGoals,
        HashSet<string> reapedGoals,
        GoalProjectionCache goalProjectionCache,
        DateTimeOffset now,
        HashSet<string> readmittedRetryReservations)
    {
        foreach (var entry in setAsideGoals.Values.ToArray())
        {
            if (onlyGoalId is not null && entry.GoalId != onlyGoalId)
            {
                continue;
            }

            var goal = kernel.Goals.FirstOrDefault(g => g.Id.Value == entry.GoalId);
            if (goal is null || IsTerminalGoal(goal))
            {
                continue;
            }

            if (entry.Condition == BatchSetAsideCondition.LifecycleEscalation &&
                RetryReservationReadmission.TrySelectExpired(
                    goal,
                    now,
                    readmittedRetryReservations,
                    out var expiredTask,
                    out var expiredReceipt))
            {
                readmittedRetryReservations.Add(expiredReceipt.ReceiptId);
                goalProjectionCache.Invalidate(goal.Id);
                setAsideGoals.Remove(entry.GoalId);
                escalatedGoals.Remove(entry.GoalId);
                reapedGoals.Remove(entry.GoalId);
                kernel.RecordGoalPolicyDecision(
                    goal.Id,
                    $"Batch loop re-admitted goal after retry reservation expired: task={expiredTask.Id.Value[..8]}; " +
                    $"receipt={expiredReceipt.ReceiptId}; expired={expiredReceipt.ReservationLeaseExpiresAt:O}.");
                continue;
            }

            if (entry.Condition == BatchSetAsideCondition.PreLandingRebaseConflict)
            {
                LandingEscalationRecheckResult recheck;
                try
                {
                    recheck = driver.RecheckPreLandingRebaseConflict(goal);
                }
                catch (Exception ex)
                {
                    var failureObservation = SanitizeReason(ex.Message);
                    kernel.RecordGoalPolicyDecision(
                        goal.Id,
                        $"Landing escalation recheck failed; goal remains set aside: {failureObservation}");
                    EmitRetryDiagnostic(
                        "ESCALATION_RECHECK_FAILED",
                        entry.GoalId[..8],
                        "pre-landing_rebase_conflict",
                        $"ESCALATION_RECHECK_FAILED goal={entry.GoalId[..8]} condition=pre-landing_rebase_conflict observation={failureObservation}");
                    continue;
                }

                CompleteRetryDiagnostic(
                    "ESCALATION_RECHECK_FAILED",
                    entry.GoalId[..8],
                    "pre-landing_rebase_conflict");

                if (recheck.TerminalUnsatisfiable)
                {
                    var terminalObservation = SanitizeReason(recheck.Observation);
                    kernel.RecordGoalPolicyDecision(
                        goal.Id,
                        $"Landing escalation recheck is terminal-unsatisfiable for this invocation: {terminalObservation}");
                    EmitProgress(
                        $"ESCALATION_RECHECK_UNSATISFIABLE goal={entry.GoalId[..8]} condition=pre-landing_rebase_conflict reason=git_merge-tree_could_not_start observation={terminalObservation}");
                    setAsideGoals.Remove(entry.GoalId);
                    excludedGoals.Add(entry.GoalId);
                    continue;
                }

                if (!recheck.ConditionResolved)
                {
                    continue;
                }

                if (string.Equals(
                    entry.LastSelfClearEvidenceFingerprint,
                    recheck.EvidenceFingerprint,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                goalProjectionCache.Invalidate(goal.Id);
                selfClearedSetAsideEntries[entry.GoalId] = entry with
                {
                    LastSelfClearEvidenceFingerprint = recheck.EvidenceFingerprint
                };
                setAsideGoals.Remove(entry.GoalId);
                escalatedGoals.Remove(entry.GoalId);
                reapedGoals.Remove(entry.GoalId);
                var observation =
                    $"status={recheck.Status}; message={SanitizeReason(recheck.Observation)}";
                kernel.RecordGoalPolicyDecision(
                    goal.Id,
                    $"Landing escalation self-cleared: condition=pre-landing_rebase_conflict; observation={observation}; evidence={recheck.EvidenceFingerprint}.");
                EmitProgress(
                    $"ESCALATION_SELF_CLEARED goal={entry.GoalId[..8]} condition=pre-landing_rebase_conflict observation={SanitizeReason(observation)}");
                continue;
            }

            if (entry.Condition == BatchSetAsideCondition.LifecycleEscalation &&
                entry.SweepBlockerKind is not null &&
                entry.SweepBlockerFingerprint is not null)
            {
                var explicitlySwept = sweepResult?.ExplicitlySweptGoalIds.Contains(goal.Id) == true;
                if (explicitlySwept)
                {
                    var currentBlockers = sweepResult!.Goals
                        .Where(result => result.GoalId == goal.Id)
                        .SelectMany(result => result.Blockers)
                        .ToArray();
                    var hasOperatorOnlyBlocker = currentBlockers.Any(blocker =>
                        blocker.Remedy.SafetyClass == TerminalGoalRemedySafetyClass.OperatorOnly);
                    var progressBlocker = SelectControllingSweepBlocker(currentBlockers.Where(blocker =>
                        blocker.Remedy.SafetyClass == TerminalGoalRemedySafetyClass.KnownSafeIdempotent));
                    var canMakeProgress = !hasOperatorOnlyBlocker &&
                        (currentBlockers.Length == 0 || progressBlocker is not null);
                    var selfClearFingerprint = progressBlocker is null
                        ? entry.SweepBlockerFingerprint
                        : BuildSweepBlockerFingerprint(progressBlocker);

                    if (canMakeProgress && !string.Equals(
                        entry.LastSelfClearEvidenceFingerprint,
                        selfClearFingerprint,
                        StringComparison.Ordinal))
                    {
                        var blockerKind = progressBlocker?.Kind ?? entry.SweepBlockerKind;
                        goalProjectionCache.Invalidate(goal.Id);
                        selfClearedSetAsideEntries[entry.GoalId] = entry with
                        {
                            LastSelfClearEvidenceFingerprint = selfClearFingerprint
                        };
                        setAsideGoals.Remove(entry.GoalId);
                        escalatedGoals.Remove(entry.GoalId);
                        reapedGoals.Remove(entry.GoalId);
                        kernel.RecordGoalPolicyDecision(
                            goal.Id,
                            $"{SetAsideSelfClearDecisionPrefix} condition=lifecycle_escalation; blocker={blockerKind}; " +
                            $"evidence={SanitizeReason(selfClearFingerprint)}.");
                        EmitProgress(
                            $"SET_ASIDE_SELF_CLEARED goal={entry.GoalId[..8]} condition=lifecycle_escalation blocker={Sanitize(blockerKind)}");
                        continue;
                    }
                }

                // A missing/partial sweep and a repeat-bounded or operator-only blocker fail closed,
                // while still preserving the pre-existing state-change readmission path below.
            }

            var currentFingerprint = entry.Condition == BatchSetAsideCondition.AwaitingClarification
                ? TryResolveLifecycleState(driver, goal) switch
                {
                    "LifecycleState=unknown" => entry.StateFingerprint,
                    nameof(GoalLifecycleState.AwaitingClarification) => BuildEscalatedGoalStateFingerprint(goal),
                    var state => $"clarification={state}"
                }
                : BuildEscalatedGoalStateFingerprint(goal);
            if (string.Equals(currentFingerprint, entry.StateFingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            goalProjectionCache.Invalidate(goal.Id);
            setAsideGoals.Remove(entry.GoalId);
            escalatedGoals.Remove(entry.GoalId);
            reapedGoals.Remove(entry.GoalId);
            kernel.RecordGoalPolicyDecision(
                goal.Id,
                $"Batch loop re-admitted escalated goal after state changed ({entry.Condition}).");
        }
    }

    private static void ReconcileUnscopedDispatchableGoals(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        string? onlyGoalId,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        HashSet<string> excludedGoals,
        HashSet<string> escalatedGoals,
        HashSet<string> reapedGoals,
        HashSet<string> completedGoals,
        Dictionary<string, int> unscopedDispatchableTicks,
        GoalProjectionCache goalProjectionCache,
        int threshold,
        int tick)
    {
        if (threshold <= 0)
        {
            threshold = DefaultUnscopedStallTickThreshold;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var goal in kernel.Goals)
        {
            var goalId = goal.Id.Value;
            seen.Add(goalId);
            if (!IsUnscopedDispatchableGoal(
                    kernel,
                    driver,
                    goal,
                    onlyGoalId,
                    setAsideGoals,
                    excludedGoals,
                    escalatedGoals,
                    completedGoals,
                    goalProjectionCache))
            {
                unscopedDispatchableTicks.Remove(goalId);
                continue;
            }

            var count = unscopedDispatchableTicks.TryGetValue(goalId, out var existing)
                ? existing + 1
                : 1;
            unscopedDispatchableTicks[goalId] = count;
            if (count < threshold)
            {
                continue;
            }

            var wasSetAside = setAsideGoals.Remove(goalId);
            var wasExcluded = excludedGoals.Remove(goalId);
            escalatedGoals.Remove(goalId);
            reapedGoals.Remove(goalId);
            goalProjectionCache.Invalidate(goal.Id);
            unscopedDispatchableTicks.Remove(goalId);
            var source = wasSetAside
                ? "set-aside"
                : wasExcluded ? "excluded" : "unscoped";
            kernel.RecordGoalPolicyDecision(
                goal.Id,
                $"Batch loop stall reconciliation tick {tick}: re-scoped dispatchable goal after {count} unscoped tick(s) ({source}).");
            EmitProgress($"STALL_RESCOPED tick={tick} goal={goalId[..8]} count={count} source={source}");
        }

        foreach (var staleGoalId in unscopedDispatchableTicks.Keys.Where(goalId => !seen.Contains(goalId)).ToArray())
        {
            unscopedDispatchableTicks.Remove(staleGoalId);
        }
    }

    private static bool IsUnscopedDispatchableGoal(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        string? onlyGoalId,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        HashSet<string> excludedGoals,
        HashSet<string> escalatedGoals,
        HashSet<string> completedGoals,
        GoalProjectionCache goalProjectionCache)
    {
        if (onlyGoalId is not null && goal.Id.Value != onlyGoalId)
        {
            return false;
        }

        if (!setAsideGoals.ContainsKey(goal.Id.Value) && !excludedGoals.Contains(goal.Id.Value))
        {
            return false;
        }

        if (goal.Status != GoalStatus.Active || IsPreWalkExcludedGoal(goal))
        {
            return false;
        }

        if (!goal.Tasks.Any(task => task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned))
        {
            return false;
        }

        if (kernel.GetPendingBlockingHumanInput(goal.Id).Count > 0)
        {
            return false;
        }

        if (GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel) is not null)
        {
            return false;
        }

        try
        {
            var state = goalProjectionCache.ResolveState(goal, driver);
            return state is GoalLifecycleState.Created or GoalLifecycleState.WorkspaceReady;
        }
        catch
        {
            return false;
        }
    }

    private static void ResetScopedGoalStallCounters(
        IReadOnlyCollection<Goal> scopedGoals,
        Dictionary<string, int> unscopedDispatchableTicks)
    {
        foreach (var goal in scopedGoals)
        {
            unscopedDispatchableTicks.Remove(goal.Id.Value);
        }
    }

    private void ReapNonTerminalEligibleGoals(
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

            ReapGoalOnce(kernel, goal, reapedGoals);
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

    private void ReapGoalOnce(AgentOrchestratorKernel kernel, Goal goal, HashSet<string> reapedGoals)
    {
        if (!reapedGoals.Add(goal.Id.Value))
        {
            return;
        }

        _reapGoalRunningDispatches(kernel, goal);
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

    private static void EmitRetryDiagnostic(
        string eventName,
        string goal,
        string condition,
        string verbatim,
        List<string>? tickLines = null)
    {
        var line = CurrentRetryDiagnostics.Value is { } diagnostics
            ? diagnostics.Observe(new RetryDiagnosticKey(eventName, goal, condition), verbatim)
            : verbatim;
        if (line is not null)
            EmitProgress(line, tickLines);
    }

    private static void CompleteWriteRetryDiagnostics(string goal, string kind, List<string>? tickLines)
    {
        CompleteRetryDiagnostic("TICK_WRITE_BUSY", goal, kind, tickLines);
        CompleteRetryDiagnostic("TICK_WRITE_DEGRADED", goal, kind, tickLines);
        CompleteRetryDiagnostic("TICK_WRITE_RETRYING", goal, kind, tickLines);
    }

    private static void CompleteRetryDiagnostic(
        string eventName,
        string goal,
        string condition,
        List<string>? tickLines = null)
    {
        var line = CurrentRetryDiagnostics.Value?.Complete(
            new RetryDiagnosticKey(eventName, goal, condition));
        if (line is not null)
            EmitProgress(line, tickLines);
    }
}

internal enum BatchSetAsideCondition
{
    AwaitingClarification,
    DependencyEscalated,
    AdvanceFault,
    LifecycleEscalation,
    PreLandingRebaseConflict
}

internal enum WatchSleepResult
{
    FallbackElapsed,
    StopRequested,
    WakeSignaled
}

internal sealed record BatchSetAsideEntry(
    string GoalId,
    BatchSetAsideCondition Condition,
    string StateFingerprint,
    string? LastSelfClearEvidenceFingerprint = null,
    string? SweepBlockerKind = null,
    string? SweepBlockerFingerprint = null);

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
