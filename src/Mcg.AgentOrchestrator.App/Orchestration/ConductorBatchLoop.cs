using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorBatchLoop
{
    internal const string StopFileName = ".conduct-stop";
    internal const int DefaultMaxVerifyRetries = 2;
    internal const int DefaultWatchIntervalSeconds = 15;
    internal const int WatchStopPollIntervalSeconds = 5;
    internal const int QuietSummaryEveryTicks = 20;
    internal const int DefaultMaxBusyWriteAttempts = 6;
    internal const int ParallelAcceptanceTransientFailureCap = 3;
    internal const int ParallelAcceptanceBoundedOvertakeLimit = 1;
    internal const int DefaultParallelAcceptanceCapacity = 4;
    internal const int DefaultUnscopedStallTickThreshold = 3;
    internal const string SelfRelaunchEnabledEnvironmentVariable = "MCG_ORCHESTRATOR_SELF_RELAUNCH_ENABLED";
    internal const bool DefaultSelfRelaunchEnabled = false;

    private readonly Func<AgentOrchestratorKernel, TerminalGoalSweepResult?> _sweep;
    private readonly Action<AgentOrchestratorKernel, Goal> _reapGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel, Goal> _detachGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel> _recoverInterruptedDispatches;
    private readonly Action<AgentOrchestratorKernel, Goal> _refreshGoalDispatchesBeforeAdvance;
    private readonly ConductorWatchProgressReporter _watchProgressReporter;
    private readonly OperatorIntentCoordinator? _operatorIntents;
    private readonly ProgressiveReviewGlanceCoordinator? _progressiveReviewGlances;
    private readonly ProgressiveReviewSteeringCoordinator? _progressiveReviewSteering;
    private readonly Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? _handoffOnMaxDuration;
    private readonly Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? _selfRelaunch;
    private readonly bool _selfRelaunchEnabled;
    private readonly ConductEventLogWriter? _conductEventLogWriter;
    private readonly Func<DateTimeOffset> _utcNow;
    private static readonly AsyncLocal<ConductEventLogWriter?> CurrentConductEventLogWriter = new();
    private static readonly object ParallelAcceptanceFairnessGate = new();
    private static string? s_parallelAcceptanceOldestWaiter;
    private static int s_parallelAcceptanceConsecutiveOvertakes;

    public ConductorBatchLoop(
        Action<AgentOrchestratorKernel>? sweep = null,
        Action<AgentOrchestratorKernel, Goal>? reapGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel, Goal>? detachGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel>? recoverInterruptedDispatches = null,
        Action<AgentOrchestratorKernel, Goal>? refreshGoalDispatchesBeforeAdvance = null,
        ConductorWatchProgressReporter? watchProgressReporter = null,
        Func<AgentOrchestratorKernel, TerminalGoalSweepResult?>? measuredSweep = null,
        Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? handoffOnMaxDuration = null,
        ConductEventLogWriter? conductEventLogWriter = null,
        Func<DateTimeOffset>? utcNow = null,
        OperatorIntentCoordinator? operatorIntents = null,
        ProgressiveReviewGlanceCoordinator? progressiveReviewGlances = null,
        ProgressiveReviewSteeringCoordinator? progressiveReviewSteering = null,
        Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? selfRelaunch = null,
        bool selfRelaunchEnabled = DefaultSelfRelaunchEnabled)
    {
        _sweep = measuredSweep ?? (kernel =>
        {
            sweep?.Invoke(kernel);
            return null;
        });
        _reapGoalRunningDispatches = reapGoalRunningDispatches ?? ((_, _) => { });
        _detachGoalRunningDispatches = detachGoalRunningDispatches ?? _reapGoalRunningDispatches;
        _recoverInterruptedDispatches = recoverInterruptedDispatches ?? (_ => { });
        _refreshGoalDispatchesBeforeAdvance = refreshGoalDispatchesBeforeAdvance ?? ((_, _) => { });
        _watchProgressReporter = watchProgressReporter ?? new ConductorWatchProgressReporter();
        _operatorIntents = operatorIntents;
        _progressiveReviewGlances = progressiveReviewGlances;
        _progressiveReviewSteering = progressiveReviewSteering;
        _handoffOnMaxDuration = handoffOnMaxDuration;
        _selfRelaunch = selfRelaunch;
        _selfRelaunchEnabled = selfRelaunchEnabled;
        _conductEventLogWriter = conductEventLogWriter;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
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
        int unscopedStallTickThreshold = DefaultUnscopedStallTickThreshold,
        Action<TimeSpan>? busyWriteDelay = null)
    {
        var previousConductEventLogWriter = CurrentConductEventLogWriter.Value;
        var previousSuccessfulLandingSink = driver.SuccessfulLandingSink;
        CurrentConductEventLogWriter.Value = _conductEventLogWriter;
        try
        {
        var excludedGoals = new HashSet<string>(StringComparer.Ordinal);
        var setAsideGoals = new Dictionary<string, BatchSetAsideEntry>(StringComparer.Ordinal);
        var completedGoals = new HashSet<string>(StringComparer.Ordinal);
        var escalatedGoals = new HashSet<string>(StringComparer.Ordinal);
        var reapedGoals = new HashSet<string>(StringComparer.Ordinal);
        var retryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastGoalDisposition = new Dictionary<string, string>(StringComparer.Ordinal);
        var unscopedDispatchableTicks = new Dictionary<string, int>(StringComparer.Ordinal);
        var goalProjectionCache = new GoalProjectionCache();
        var totalTicks = 0;
        var totalAdvanced = 0;
        var totalHeld = 0;
        var totalEscalated = 0;
        var totalRetried = 0;
        var totalDone = 0;
        var stopRequested = false;
        var maxDurationReached = false;
        ConductorSelfRelaunchRequest? pendingSelfRelaunch = null;
        ConductorSelfRelaunchRequest? deferredSelfRelaunch = null;
        int? selfRelaunchRetryAfterTick = null;
        DateTimeOffset? selfRelaunchDrainStartedAt = null;
        ConductorLoopHandoffResult? selfRelaunchHandoff = null;
        var started = _utcNow();
        var initiallyCompletedGoalIds = GetCompletedGoalIds(kernel);
        if (_selfRelaunchEnabled && _selfRelaunch is not null)
        {
            driver.SuccessfulLandingSink = receipt =>
            {
                var changes = RepositoryChangeClassifier.Classify(receipt.ChangedFiles);
                if (changes.RequiresConductorRelaunch)
                {
                    pendingSelfRelaunch = new ConductorSelfRelaunchRequest(receipt.GoalId, totalTicks);
                    deferredSelfRelaunch = null;
                    selfRelaunchRetryAfterTick = null;
                    selfRelaunchDrainStartedAt ??= _utcNow();
                    EmitProgress(
                        $"LOOP_RELAUNCH_SCHEDULED tick={totalTicks} goal={receipt.GoalId} " +
                        $"changedFiles={receipt.ChangedFiles.Count} coalesced=true");
                }

                previousSuccessfulLandingSink?.Invoke(receipt);
            };
        }
        EmitProgress($"LOOP_START policy={Sanitize(policy.Name)} maxIterations={maxIterations?.ToString() ?? "none"} maxDurationSeconds={(maxDuration.HasValue ? ((int)maxDuration.Value.TotalSeconds).ToString() : "none")}");

        while (true)
        {
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
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-file");
                Console.WriteLine($"[conduct --loop] Stop signal detected at tick {totalTicks + 1}; no new dispatches will be started.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop", null, busyWriteDelay);
                break;
            }

            if (pendingSelfRelaunch is null &&
                maxIterations.HasValue &&
                totalTicks >= maxIterations.Value)
            {
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=max-iter max={maxIterations.Value}");
                Console.WriteLine($"[conduct --loop] Max iterations ({maxIterations.Value}) reached after {totalTicks} ticks.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "max-iterations", null, busyWriteDelay);
                break;
            }

            if (pendingSelfRelaunch is null &&
                maxDuration.HasValue &&
                _utcNow() - started >= maxDuration.Value)
            {
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=max-duration seconds={(int)maxDuration.Value.TotalSeconds}");
                Console.WriteLine($"[conduct --loop] Max duration ({maxDuration.Value.TotalSeconds:0}s) reached after {totalTicks} ticks.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "max-duration", null, busyWriteDelay);
                maxDurationReached = true;
                break;
            }

            var nextTick = totalTicks + 1;
            var preTickTimingLines = new List<string>();
            var sweepClock = Stopwatch.StartNew();
            var sweepResult = RunJanitorialPhase("sweep", nextTick, () => _sweep(kernel));
            RunJanitorialPhase("recover-interrupted-dispatches", nextTick, () =>
            {
                _recoverInterruptedDispatches(kernel);
                return true;
            });
            if (pendingSelfRelaunch is not null)
            {
                var activeDispatches = CountRunningDispatches(kernel, onlyGoalId);
                if (activeDispatches > 0)
                {
                    var drainElapsed = _utcNow() - (selfRelaunchDrainStartedAt ?? _utcNow());
                    if (drainElapsed >= DispatchRecoveryPolicy.DefaultLiveIdleTimeout)
                    {
                        EmitSelfRelaunchRollback(
                            totalTicks,
                            pendingSelfRelaunch.GoalId,
                            "drain",
                            $"active dispatches did not reach terminal receipts within {(int)DispatchRecoveryPolicy.DefaultLiveIdleTimeout.TotalMinutes} minutes");
                        deferredSelfRelaunch = pendingSelfRelaunch;
                        selfRelaunchRetryAfterTick = totalTicks + 1;
                        pendingSelfRelaunch = null;
                        selfRelaunchDrainStartedAt = null;
                    }

                    if (pendingSelfRelaunch is not null)
                    {
                        EmitProgress(
                            $"LOOP_RELAUNCH_DRAIN tick={totalTicks} goal={pendingSelfRelaunch.GoalId} active={activeDispatches} admitting=false");
                        TryPersistCheckpoint(
                            persistTick,
                            persistGoalTick,
                            kernel,
                            totalTicks,
                            onlyGoalId,
                            "self-relaunch-drain",
                            null,
                            busyWriteDelay);
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
                        continue;
                    }
                }

                if (pendingSelfRelaunch is not null)
                {
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
                        throw new InvalidOperationException(
                            "Self-relaunch failed without confirming incumbent authority; refusing to continue the conductor loop.",
                            ex);
                    }
                    if (relaunchResult.HandedOff)
                    {
                        selfRelaunchHandoff = relaunchResult.Handoff;
                        EmitHandoffProgress(totalTicks, relaunchResult.Handoff!, pendingSelfRelaunch.GoalId);
                        break;
                    }

                    if (!relaunchResult.IncumbentCanContinue)
                    {
                        EmitProgress(
                            $"LOOP_HANDOFF_FAILED tick={totalTicks} goal={pendingSelfRelaunch.GoalId} phase=handoff " +
                            $"rolledBack=false continuing=false reason={SanitizeHandoffDetail(relaunchResult.Reason ?? "rollback authority was not confirmed")}");
                        throw new InvalidOperationException(
                            "Self-relaunch rollback did not confirm incumbent authority; refusing to continue the conductor loop.");
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
                }
            }
            ReadmitResolvedSetAsideGoals(kernel, driver, onlyGoalId, setAsideGoals, escalatedGoals, reapedGoals, goalProjectionCache);
            MarkCompletedDependencyGoals(kernel, driver, onlyGoalId, completedGoals, goalProjectionCache);
            ReconcileUnscopedDispatchableGoals(
                kernel,
                driver,
                onlyGoalId,
                setAsideGoals,
                excludedGoals,
                escalatedGoals,
                reapedGoals,
                completedGoals,
                unscopedDispatchableTicks,
                goalProjectionCache,
                unscopedStallTickThreshold,
                nextTick);
            sweepClock.Stop();
            preTickTimingLines.Add(FormatPhaseTiming(nextTick, "sweep", sweepClock.Elapsed,
                $"goals={kernel.Goals.Count} completed_dependencies={completedGoals.Count} set_aside={setAsideGoals.Count}{FormatSweepCacheDetail(sweepResult)}"));

            var preWalkClock = Stopwatch.StartNew();
            var actionableIntentGoalIds = new HashSet<string>(StringComparer.Ordinal);
            var preWalkIntentLines = new List<string>();
            var preWalkIntentProcessed = false;
            if (_operatorIntents is not null)
            {
                try
                {
                    actionableIntentGoalIds.UnionWith(_operatorIntents.ListActionableGoalIds());
                }
                catch (Exception ex)
                {
                    EmitProgress(
                        $"OPERATOR_INTENT result=store-unavailable phase=list reason={Sanitize(ex.Message)}");
                }
            }

            var scopedGoals = kernel.Goals
                .Where(g => (onlyGoalId is null || g.Id.Value == onlyGoalId)
                    && (!excludedGoals.Contains(g.Id.Value) || actionableIntentGoalIds.Contains(g.Id.Value))
                    && (!setAsideGoals.ContainsKey(g.Id.Value) || actionableIntentGoalIds.Contains(g.Id.Value)))
                .ToArray();
            var scopedGoalsById = scopedGoals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
            var preWalkIntentChangedGoalIds = new HashSet<GoalId>();
            if (_operatorIntents is not null)
            {
                foreach (var actionableGoalId in actionableIntentGoalIds)
                {
                    if (!scopedGoalsById.TryGetValue(actionableGoalId, out var scopedGoal))
                    {
                        var reason = kernel.Goals.Any(goal => goal.Id.Value == actionableGoalId)
                            ? $"goal is outside conductor scope {ShortGoalId(onlyGoalId!)}"
                            : "goal was not found in conductor state";
                        try
                        {
                            var rejectedLines = _operatorIntents.RejectPending(actionableGoalId, reason);
                            preWalkIntentLines.AddRange(rejectedLines);
                            preWalkIntentProcessed |= rejectedLines.Count > 0;
                        }
                        catch (Exception ex)
                        {
                            EmitProgress(
                                $"OPERATOR_INTENT goal={ShortGoalId(actionableGoalId)} result=store-unavailable phase=reject reason={Sanitize(ex.Message)}");
                        }

                        continue;
                    }

                    OperatorIntentExecutionResult intentResult;
                    try
                    {
                        intentResult = _operatorIntents.ExecutePending(kernel, scopedGoal);
                    }
                    catch (Exception ex)
                    {
                        EmitProgress(
                            $"OPERATOR_INTENT goal={ShortGoalId(scopedGoal.Id.Value)} result=store-unavailable phase=execute reason={Sanitize(ex.Message)}");
                        continue;
                    }

                    preWalkIntentLines.AddRange(intentResult.ProgressLines);
                    preWalkIntentProcessed |= intentResult.ProgressLines.Count > 0;
                    if (intentResult.MutatedGoalState)
                    {
                        preWalkIntentChangedGoalIds.Add(scopedGoal.Id);
                        excludedGoals.Remove(scopedGoal.Id.Value);
                        setAsideGoals.Remove(scopedGoal.Id.Value);
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
                .Where(g => IsLoopEligibleGoal(g, driver, goalProjectionCache))
                .ToArray();
            ResetScopedGoalStallCounters(eligible, unscopedDispatchableTicks);
            preWalkClock.Stop();
            preTickTimingLines.Add(FormatPhaseTiming(nextTick, "prewalk", preWalkClock.Elapsed,
                $"scoped={scopedGoals.Length} candidates={preWalkCandidates.Length} eligible={eligible.Length} excluded_parked={parkedExcludedCount} excluded_terminal={terminalExcludedCount} cache_entries={goalProjectionCache.Count}"));

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
                    if (preWalkIntentChangedGoalIds.Count > 0 && persistGoalTick is not null)
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

                    if (intentStatePersisted && preWalkIntentChangedGoalIds.Count > 0)
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
                        EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-after-operator-intent");
                        DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                        TryPersistCheckpoint(
                            persistTick,
                            persistGoalTick,
                            kernel,
                            totalTicks,
                            onlyGoalId,
                            "stop-after-operator-intent",
                            null,
                            busyWriteDelay);
                        break;
                    }

                    if (!(keepAliveWhenIdle && watchInterval is not null))
                    {
                        continue;
                    }
                }

                // Daemon keep-alive: when configured (and watching), an empty backlog is NOT a reason to
                // exit — sleep and keep polling so goals submitted later are ingested by the sweep and
                // driven. A one-shot `conduct --loop` (keepAliveWhenIdle=false) still completes here.
                if (keepAliveWhenIdle && watchInterval is not null)
                {
                    var idleInterval = GetWatchFallbackInterval(kernel, onlyGoalId, watchInterval.Value);
                    EmitProgress($"IDLE_SLEEP seconds={(int)idleInterval.TotalSeconds}");
                    var idleSleep = sleepFunc is not null
                        ? (sleepFunc(idleInterval) ? WatchSleepResult.StopRequested : WatchSleepResult.FallbackElapsed)
                        : SleepUntilNextTick(idleInterval, stopFilePath, wakeSignal, GetRunningDispatchExitCodePaths(kernel, onlyGoalId));
                    if (idleSleep == WatchSleepResult.WakeSignaled)
                    {
                        RunJanitorialPhase("idle-wake-sweep", nextTick, () => _sweep(kernel));
                        RunJanitorialPhase("idle-wake-recover-interrupted-dispatches", nextTick, () =>
                        {
                            _recoverInterruptedDispatches(kernel);
                            return true;
                        });
                        TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "idle-wake-sweep", null, busyWriteDelay);
                    }

                    if (idleSleep == WatchSleepResult.StopRequested || IsStopRequested(stopFilePath))
                    {
                        stopRequested = true;
                        EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-while-idle");
                        DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                        TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop-while-idle", null, busyWriteDelay);
                        break;
                    }

                    continue;
                }

                EmitProgress($"LOOP_STOP tick={totalTicks} reason=all-done-or-escalated");
                Console.WriteLine($"[conduct --loop] All goals done or escalated; loop complete after {totalTicks} ticks.");
                break;
            }

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
            var parallelLandingResults = RunParallelAcceptanceBatch(
                eligible,
                kernel,
                driver,
                policy,
                completedGoals,
                escalatedGoals,
                totalTicks,
                changedGoalLines,
                changedGoalIds);

            var previousPhaseTimingSink = driver.PhaseTimingSink;
            var perGoalPhaseTimingLines = new List<string>();
            driver.PhaseTimingSink = line => perGoalPhaseTimingLines.Add($"PHASE_TIMING tick={totalTicks} {line}");
            var goalWalkTimings = new List<GoalWalkTiming>();
            var goalWalkClock = Stopwatch.StartNew();
            var glanceDurationStats = _progressiveReviewGlances is null
                ? Array.Empty<TaskDurationStatsRecord>()
                : kernel.BuildTaskDurationStats();
            foreach (var goal in eligible)
            {
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

                // Dependency gate: check all DependsOn goals before advancing.
                var depHoldReason = GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel);
                if (depHoldReason is not null)
                {
                    var progressLine = $"GOAL goal={label} result=held reason={Sanitize(depHoldReason)}";
                    if (RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → held: {depHoldReason}");
                        kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: held: {depHoldReason}");
                    }
                    // A goal held due to a failed/escalated dependency will never unblock unless
                    // future condition-specific re-entry logic says otherwise.
                    if (depHoldReason.StartsWith("dependency escalated", StringComparison.Ordinal))
                    {
                        escalatedGoals.Add(goal.Id.Value);
                        SetAside(kernel, driver, goal, BatchSetAsideCondition.DependencyEscalated, setAsideGoals);
                        ReapGoalOnce(kernel, goal, reapedGoals);
                        tickEscalated++;
                    }
                    else
                    {
                        tickHeld++;
                    }

                    FinishGoalWalk("dependency-held");
                    continue;
                }

                if (HasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver))
                {
                    var progressLine = $"GOAL goal={label} result=escalated state={GoalLifecycleState.Verified}";
                    if (RecordChangedDisposition(goal.Id.Value, progressLine, lastGoalDisposition, changedGoalLines))
                    {
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated at Verified — acceptance verification already requires operator action.");
                    }

                    escalatedGoals.Add(goal.Id.Value);
                    SetAside(kernel, driver, goal, BatchSetAsideCondition.LifecycleEscalation, setAsideGoals);
                    ReapGoalOnce(kernel, goal, reapedGoals);
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
                    try
                    {
                        var beforeRefresh = BuildEscalatedGoalStateFingerprint(kernel, driver, goal);
                        _refreshGoalDispatchesBeforeAdvance(kernel, goal);
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
                            var steerResult = _progressiveReviewSteering.ExecutePending(kernel, goal);
                            foreach (var line in steerResult.ProgressLines)
                            {
                                EmitProgress(line, tickLines);
                            }

                            if (steerResult.MutatedTaskState)
                            {
                                changedGoalIds.Add(goal.Id);
                                tickHeld++;
                                goalProjectionCache.Invalidate(goal.Id);
                                FinishGoalWalk("progressive-review-steer");
                                continue;
                            }
                        }

                        goalProjectionCache.Invalidate(goal.Id);
                        var afterRefresh = BuildEscalatedGoalStateFingerprint(kernel, driver, goal);
                        if (!string.Equals(beforeRefresh, afterRefresh, StringComparison.Ordinal))
                        {
                            changedGoalIds.Add(goal.Id);
                        }

                        result = driver.AdvanceOnce(goal, policy);
                    }
                    catch (Exception ex)
                    {
                        if (ConductorDriver.IsCriticalDispatchRecordWriteFailure(ex))
                            throw;

                        var msg = $"Batch loop tick {totalTicks}: fault isolating goal — advance threw: {Sanitize(ex.Message)}";
                        changedGoalLines.Add($"GOAL goal={label} result=escalated reason={Sanitize(ex.Message)}");
                        lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated (advance threw): {ex.Message}");
                        kernel.RecordGoalPolicyDecision(goal.Id, msg);
                        escalatedGoals.Add(goal.Id.Value);
                        SetAside(kernel, driver, goal, BatchSetAsideCondition.AdvanceFault, setAsideGoals);
                        ReapGoalOnce(kernel, goal, reapedGoals);
                        tickEscalated++;
                        FinishGoalWalk("advance-fault");
                        continue;
                    }
                }

                // Auto-retry transient acceptance verification failures (up to maxVerifyRetries re-verifications)
                var serialRetryRan = false;
                if (result.WasEscalated && IsTransientVerificationFailure(result))
                {
                    retryCounts.TryGetValue(goal.Id.Value, out var retries);
                    while (retries < maxVerifyRetries && result.WasEscalated && IsTransientVerificationFailure(result))
                    {
                        serialRetryRan = true;
                        retries++;
                        retryCounts[goal.Id.Value] = retries;
                        tickRetried++;
                        changedGoalLines.Add($"GOAL goal={label} result=retry attempt={retries}/{maxVerifyRetries}");
                        lastGoalDisposition[goal.Id.Value] = changedGoalLines[^1];
                        changedGoalIds.Add(goal.Id);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {goal.Id.Value[..8]} acceptance flake (retry {retries}/{maxVerifyRetries})");
                        kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop auto-retry acceptance verification (attempt {retries}/{maxVerifyRetries})");
                        result = driver.AdvanceOnce(goal, policy);
                        goalProjectionCache.Invalidate(goal.Id);
                    }
                }

                if (TryReconcileAwaitingVerificationHold(kernel, goal, result, totalTicks, out var reconciledOutcome))
                {
                    changedGoalIds.Add(goal.Id);
                    result = result with { Outcome = reconciledOutcome };
                    goalProjectionCache.Invalidate(goal.Id);
                }

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
                    kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {FormatOutcome(result.Outcome)}");
                }

                if (result.WasExecuted)        { tickAdvanced++; }
                else if (result.IsHeld)        { tickHeld++; }
                else if (result.WasEscalated)  { tickEscalated++; escalatedGoals.Add(goal.Id.Value); SetAside(kernel, driver, goal, GetSetAsideCondition(result), setAsideGoals); ReapGoalOnce(kernel, goal, reapedGoals); }
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

            // Durably checkpoint this tick's progress (dispatches started, reconcile results, escalations).
            // Without this the loop's mutations live only in memory until the whole command returns, so a
            // long-running watch loop never persists and a killed loop loses every dispatch on rollback —
            // the goal then re-dispatches the same stage forever and can never advance.
            // When persistGoalTick is supplied, persist only the goals whose disposition changed this tick
            // in one bulk checkpoint. The per-goal loop above is justified because it runs each goal's
            // state machine; the durable write is intentionally batched.
            if (persistGoalTick is not null)
            {
                if (changedGoalIds.Count > 0)
                {
                    PersistGoalTickOrThrow(persistGoalTick, kernel, changedGoalIds.ToArray(), totalTicks, tickLines, busyWriteDelay);
                    CompletePersistedOperatorIntents(changedGoalIds, tickLines);
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
                    EmitProgress($"LOOP_STOP tick={totalTicks} reason=no-progress-no-watch");
                    Console.WriteLine($"[conduct --loop] No progress in tick {totalTicks}; all eligible goals held or escalated.");
                    DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "no-progress", tickLines, busyWriteDelay);
                    onTick?.Invoke(tickSummary);
                    break;
                }

                var fallbackInterval = GetWatchFallbackInterval(kernel, onlyGoalId, watchInterval.Value);
                var sleepSeconds = (int)fallbackInterval.TotalSeconds;
                if (emitTickSummary)
                {
                    EmitProgress($"WATCH_SLEEP tick={totalTicks} seconds={sleepSeconds}");
                    Console.WriteLine($"[conduct --loop --watch] No progress in tick {totalTicks}; sleeping {sleepSeconds}s for workers to complete.");
                }
                onTick?.Invoke(tickSummary with { WatchSleeping = true });

                var sleepResult = sleepFunc is not null
                    ? (sleepFunc(fallbackInterval) ? WatchSleepResult.StopRequested : WatchSleepResult.FallbackElapsed)
                    : SleepUntilNextTick(fallbackInterval, stopFilePath, wakeSignal, GetRunningDispatchExitCodePaths(kernel, onlyGoalId));

                if (sleepResult == WatchSleepResult.WakeSignaled)
                {
                    RunJanitorialPhase("wake-sweep", totalTicks, () => _sweep(kernel));
                    RunJanitorialPhase("wake-recover-interrupted-dispatches", totalTicks, () =>
                    {
                        _recoverInterruptedDispatches(kernel);
                        return true;
                    });
                    TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "wake-sweep", tickLines, busyWriteDelay);
                }

                if (sleepResult == WatchSleepResult.StopRequested || IsStopRequested(stopFilePath))
                {
                    stopRequested = true;
                    EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-file-during-sleep");
                    Console.WriteLine($"[conduct --loop --watch] Stop signal detected during sleep after tick {totalTicks}; no new dispatches.");
                    DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    TryPersistCheckpoint(persistTick, persistGoalTick, kernel, totalTicks, onlyGoalId, "stop-during-sleep", tickLines, busyWriteDelay);
                    break;
                }

                continue;
            }

            onTick?.Invoke(tickSummary);
        }

        ConductorLoopHandoffResult? handoff = selfRelaunchHandoff;
        if (maxDurationReached && _handoffOnMaxDuration is not null)
        {
            var landedGoalDelta = GetCompletedGoalIds(kernel).Except(initiallyCompletedGoalIds, StringComparer.Ordinal).Count();
            var request = new ConductorLoopHandoffRequest(totalTicks, maxDuration ?? TimeSpan.Zero, totalDone, landedGoalDelta);
            handoff = _handoffOnMaxDuration(request);
            EmitHandoffProgress(totalTicks, handoff);
        }

        return new BatchLoopSummary(totalTicks, totalAdvanced, totalHeld, totalEscalated, totalRetried, totalDone, stopRequested, handoff);
        }
        finally
        {
            driver.SuccessfulLandingSink = previousSuccessfulLandingSink;
            CurrentConductEventLogWriter.Value = previousConductEventLogWriter;
        }
    }

    internal static bool ResolveSelfRelaunchEnabled(string? configuredValue) =>
        bool.TryParse(configuredValue, out var enabled) && enabled;

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

        EmitProgress($"LOOP_HANDOFF_SKIPPED tick={tick} reason={Sanitize(handoff.Reason ?? "not-started")}");
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

    private static T? RunJanitorialPhase<T>(string phase, int tick, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            EmitProgress($"LOOP_JANITORIAL_FAILED tick={tick} phase={SanitizeHandoffDetail(phase)} exception={ex.GetType().Name} message={SanitizeHandoffDetail(ex.Message)}");
            return default;
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

        var required = kind == "loop-relaunch-rollback" ||
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
            "ACCEPTANCE_LEASE_RELEASE" => "acceptance-lease",
            "ACCEPTANCE_LEASE_YIELD" => "acceptance-lease",
            "BUILD_LOCK_BLOCKED" => "lock-blocker",
            "GOAL" => ClassifyGoalEvent(line),
            "LOCK" => "lock-blocker",
            "LOOP_HANDOFF" => "loop-handoff",
            "LOOP_HANDOFF_FAILED" => "loop-handoff",
            "LOOP_HANDOFF_PENDING" => "loop-handoff",
            "LOOP_HANDOFF_SKIPPED" => "loop-handoff",
            "LOOP_RELAUNCH_SCHEDULED" => "loop-relaunch",
            "LOOP_RELAUNCH_DRAIN" => "loop-relaunch",
            "LOOP_RELAUNCH_REBUILD" => "loop-relaunch",
            "LOOP_RELAUNCH_ROLLBACK" => "loop-relaunch-rollback",
            "LOOP_JANITORIAL_FAILED" => "loop-janitorial-failure",
            "LOOP_START" => "loop-start",
            "LOOP_STOP" => "loop-stop",
            "TICK_WRITE_BUSY" => "lock-blocker",
            "TICK_WRITE_DEGRADED" => "lock-blocker",
            "GLANCE" => "progressive-review-glance",
            "WATCH_TRANSITION" => "watch-transition",
            _ => string.Empty
        };

        return kind.Length > 0;
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
            ? string.Empty
            : $" sweep_cache_hits={result.CacheHitCount} sweep_cache_misses={result.CacheMissCount}";

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
        TaskId taskId)
    {
        var tickLines = new List<string>();
        if (!TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, [goalId]),
            tick: 0,
            goals: ShortGoalId(goalId.Value),
            kind: "dispatch-start",
            tickLines,
            busyWriteDelay: null))
        {
            ThrowCriticalPersistFailure("dispatch-start", ShortGoalId(goalId.Value), taskId);
        }
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
                $"OPERATOR_INTENT goals={ResolveGoalContext(persistedGoalIds, onlyGoalId: null)} result=completion-deferred reason={Sanitize(ex.Message)}";
            EmitProgress(line, tickLines);
        }
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
        if (!TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, changedGoalIds),
            tick,
            goals,
            "goal",
            tickLines,
            busyWriteDelay))
        {
            ThrowCriticalPersistFailure("goal", goals, taskId: null);
        }
    }

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
        Action<TimeSpan>? busyWriteDelay)
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
            busyWriteDelay);
    }

    private static bool TryPersistCheckpoint(
        Action<AgentOrchestratorKernel>? persistTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        AgentOrchestratorKernel kernel,
        int tick,
        string? onlyGoalId,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay)
    {
        if (persistGoalTick is null)
        {
            return TryPersistTick(
                persistTick,
                kernel,
                tick,
                ResolveGoalContext(kernel, onlyGoalId),
                kind,
                tickLines,
                busyWriteDelay);
        }

        var goalIds = ResolveCheckpointGoalIds(kernel, onlyGoalId);
        if (goalIds.Length == 0)
        {
            return true;
        }

        return TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, goalIds),
            tick,
            ResolveGoalContext(goalIds, onlyGoalId),
            kind,
            tickLines,
            busyWriteDelay);
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
        Action<TimeSpan>? busyWriteDelay)
    {
        var delay = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; attempt <= DefaultMaxBusyWriteAttempts; attempt++)
        {
            try
            {
                persist();
                return true;
            }
            catch (Exception ex) when (IsTransientSqliteLock(ex))
            {
                EmitProgress(
                    $"TICK_WRITE_BUSY tick={tick} kind={kind} goal={goals} attempt={attempt} likelyHolder=concurrent-per-command-host",
                    tickLines);

                if (attempt == DefaultMaxBusyWriteAttempts)
                {
                    EmitProgress(
                        $"TICK_WRITE_DEGRADED tick={tick} kind={kind} goal={goals} attempt={attempt} likelyHolder=concurrent-per-command-host error={Sanitize(ex.Message)}",
                        tickLines);
                    return false;
                }

                if (busyWriteDelay is null)
                    Thread.Sleep(delay);
                else
                    busyWriteDelay(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }

        return false;
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

    private static bool IsTransientSqliteLock(Exception ex)
    {
        if (IsSqliteBusyOrLocked(ex))
        {
            return true;
        }

        return ex.InnerException is not null && IsTransientSqliteLock(ex.InnerException);
    }

    private static bool IsSqliteBusyOrLocked(Exception ex)
    {
        var typeName = ex.GetType().FullName;
        if (string.Equals(typeName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal)
            && TryGetSqliteErrorCode(ex, out var sqliteErrorCode)
            && sqliteErrorCode is 5 or 6)
        {
            return true;
        }

        return ex.Message.Contains("SQLite Error 5", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("SQLite Error 6", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("database table is locked", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSqliteErrorCode(Exception ex, out int sqliteErrorCode)
    {
        sqliteErrorCode = 0;
        var property = ex.GetType().GetProperty("SqliteErrorCode");
        if (property?.GetValue(ex) is int value)
        {
            sqliteErrorCode = value;
            return true;
        }

        return false;
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

    private static string SanitizeHandoffDetail(string value) =>
        value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    private static IReadOnlyDictionary<string, ParallelLandingOutcome> RunParallelAcceptanceBatch(
        IReadOnlyList<Goal> eligible,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds)
    {
        var trustedHostSlotCount = DefaultParallelAcceptanceCapacity;
        if (trustedHostSlotCount < 2)
        {
            return new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        }

        var activeCandidates = new List<ConductorParallelAcceptanceCandidate>();
        var results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        var deferredByAdmission = 0;
        var orderedEligible = OrderParallelAcceptanceEligibleGoals(eligible
            .Where(goal =>
                IsParallelAcceptanceLifecycleEligible(goal, driver) &&
                goal.Status is GoalStatus.Verified or GoalStatus.Verifying &&
                HasCompletedPassedVerificationForAllTasks(goal) &&
                GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel) is null &&
                TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver) == false)
            .ToArray());
        var oldestWaiter = orderedEligible.FirstOrDefault();
        var oldestServedThisTick = false;
        foreach (var goal in orderedEligible)
        {
            int acceptanceSlotCount;
            try
            {
                acceptanceSlotCount = driver.GetAcceptanceSlotCount(goal);
                if (acceptanceSlotCount is < 1 || acceptanceSlotCount > trustedHostSlotCount)
                {
                    throw new InvalidDataException(
                        $"Acceptance slot count must be between 1 and the trusted host maximum {trustedHostSlotCount}.");
                }
            }
            catch (Exception ex)
            {
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    driver.EscalateParallelLandingAcceptance(
                        goal,
                        policy,
                        $"invalid parallel acceptance slot settings: {Sanitize(ex.Message)}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=parallel-acceptance-slot-settings goal={goal.Id.Value[..8]} detail={Sanitize(ex.Message)}",
                    changedGoalLines);
                continue;
            }

            if (oldestWaiter is not null &&
                goal.Id != oldestWaiter.Id &&
                !oldestServedThisTick &&
                ShouldDeferForParallelAcceptanceFairness(oldestWaiter.Id.Value))
            {
                var deferredCandidate = TryBuildParallelAcceptanceCandidate(
                    driver,
                    goal,
                    policy,
                    Math.Min(activeCandidates.Count, acceptanceSlotCount - 1),
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
                    results[goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            goal,
                            policy,
                            $"parallel acceptance candidate unavailable; retry on next conduct tick: {Sanitize(deferredBuildException.Message)}"),
                        null);
                    RecordParallelAcceptanceProgress(
                        $"ADMISSION tick={tick} result=held reason=parallel-acceptance-candidate goal={goal.Id.Value[..8]} detail={Sanitize(deferredBuildException.Message)}",
                        changedGoalLines);
                }

                continue;
            }

            if (activeCandidates.Count >= acceptanceSlotCount)
            {
                deferredByAdmission++;
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        $"candidate manifest slot cap {acceptanceSlotCount} reached; retry on next conduct tick"),
                    null);
                continue;
            }

            var candidate = TryBuildParallelAcceptanceCandidate(
                driver,
                goal,
                policy,
                activeCandidates.Count,
                out var buildException);
            if (candidate is null)
            {
                if (buildException is not null)
                {
                    results[goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            goal,
                            policy,
                            $"parallel acceptance candidate unavailable; retry on next conduct tick: {Sanitize(buildException.Message)}"),
                        null);
                    RecordParallelAcceptanceProgress(
                        $"ADMISSION tick={tick} result=held reason=parallel-acceptance-candidate goal={goal.Id.Value[..8]} detail={Sanitize(buildException.Message)}",
                        changedGoalLines);
                }

                continue;
            }

            if (activeCandidates.Count >= trustedHostSlotCount)
            {
                deferredByAdmission++;
                results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        candidate,
                        policy,
                        "parallel acceptance slot cap reached; retry on next conduct tick"),
                    null);
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

            var decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                driver.RunParallelLandingAcceptance);
            ReplayParallelAcceptanceLeaseReceipts(driver, decision.Attempt, changedGoalLines);

            switch (decision.Kind)
            {
                case ConductorParallelAcceptanceAttemptDecisionKind.Started:
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
                            var terminalResult = CompleteParallelAcceptanceRun(driver, policy, terminalRun);
                            driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(terminalDecision.Attempt);
                            oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                            RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                            RecordParallelAcceptanceFairnessCapIfReached(goal, oldestWaiter, tick, changedGoalLines);
                            results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(terminalResult, candidate.SlotIndex);
                            RecordParallelAcceptanceProgress(
                                $"ACCEPTANCE goal={candidate.GoalPrefix} slot=slot-{candidate.SlotIndex} result={AcceptanceRunDisposition(terminalRun)} attempt={terminalDecision.Attempt.AttemptId} tick={tick}",
                                changedGoalLines);
                            break;
                        }

                        ReconcileParallelAcceptanceTerminalState(kernel, goal, terminalDecision.Attempt);
                        results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                            ParallelAcceptanceTerminal(driver, candidate, policy, terminalDecision.Attempt),
                            candidate.SlotIndex);
                        driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(terminalDecision.Attempt);
                        RecordParallelAcceptanceProgress(
                            $"ACCEPTANCE goal={candidate.GoalPrefix} slot=slot-{candidate.SlotIndex} result={AcceptanceAttemptOutcomeToken(terminalDecision.Attempt.Outcome)} attempt={terminalDecision.Attempt.AttemptId} tick={tick}",
                            changedGoalLines);
                        break;
                    }

                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    activeCandidates.Add(candidate);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessCapIfReached(goal, oldestWaiter, tick, changedGoalLines);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification running in background"),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        $"ACCEPTANCE goal={candidate.GoalPrefix} slot=slot-{candidate.SlotIndex} result=started attempt={decision.Attempt.AttemptId} tick={tick}",
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Running:
                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    activeCandidates.Add(candidate);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessCapIfReached(goal, oldestWaiter, tick, changedGoalLines);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification still running in background"),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        $"ACCEPTANCE goal={candidate.GoalPrefix} slot=slot-{candidate.SlotIndex} result=running attempt={decision.Attempt.AttemptId} tick={tick}",
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Completed:
                    var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                        candidate,
                        new InvalidOperationException("Completed acceptance attempt had no run result."));
                    ReconcileParallelAcceptanceTerminalState(kernel, goal, run, decision.Attempt);
                    var result = CompleteParallelAcceptanceRun(driver, policy, run);
                    driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessCapIfReached(goal, oldestWaiter, tick, changedGoalLines);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(result, candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        $"ACCEPTANCE goal={candidate.GoalPrefix} slot=slot-{candidate.SlotIndex} result={AcceptanceRunDisposition(run)} attempt={decision.Attempt.AttemptId} tick={tick}",
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun:
                    ReconcileParallelAcceptanceTerminalState(kernel, goal, decision.Attempt);
                    results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceTerminal(driver, candidate, policy, decision.Attempt),
                        candidate.SlotIndex);
                    driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
                    RecordParallelAcceptanceProgress(
                        $"ACCEPTANCE goal={candidate.GoalPrefix} slot=slot-{candidate.SlotIndex} result={AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome)} attempt={decision.Attempt.AttemptId} tick={tick}",
                        changedGoalLines);
                    break;
            }
        }

        if (deferredByAdmission > 0)
        {
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=deferred reason=parallel-acceptance-slot-cap cap={trustedHostSlotCount} deferred={deferredByAdmission}",
                changedGoalLines);
        }

        return results;
    }

    private static bool MarkParallelAcceptanceStarted(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttempt attempt,
        int tick)
    {
        if (goal.Status != GoalStatus.Verified)
        {
            return false;
        }

        return kernel.BeginGoalAcceptanceVerification(
            goal.Id,
            $"Batch loop tick {tick}: acceptance gate record {attempt.AttemptId} is running in background; goal entered Verifying.");
    }

    private static void ReconcileParallelAcceptanceTerminalState(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        EnsureParallelAcceptanceTerminalIsVerifying(kernel, goal, attempt);
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

        kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            BuildFailedAcceptanceChecks(run, attempt),
            $"Batch loop reconciled background acceptance gate {attempt.AttemptId} terminal artifact ({disposition}); goal moved to AcceptanceFailed.",
            run.Candidate.BranchHeadSha ?? attempt.BranchHeadSha,
            run.Candidate.MainHeadSha ?? attempt.MainHeadSha);
    }

    private static void ReconcileParallelAcceptanceTerminalState(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttempt attempt)
    {
        EnsureParallelAcceptanceTerminalIsVerifying(kernel, goal, attempt);
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
        run.Exception is DotnetBuildSlotsBusyException or BuildLockBlockedException or OperationCanceledException;

    private static bool IsEnvironmentInterferenceAcceptanceRun(ConductorParallelAcceptanceRunResult run) =>
        run.Acceptance?.RequiredUnmetCriteria.Any(check =>
            string.Equals(
                check.FailureClassification,
                AcceptanceFailureClassifications.GateEnvironmentInterference,
                StringComparison.Ordinal)) == true;

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
            return [$"background-acceptance-fault: {Sanitize(run.Exception.Message)}"];
        }

        if (run.EarlyOutcome is not null)
        {
            return [$"{run.EarlyOutcome.Kind}: {Sanitize(run.EarlyOutcome.Detail)}"];
        }

        return [AcceptanceAttemptFailureCheck(attempt)];
    }

    private static string AcceptanceAttemptFailureCheck(ConductorParallelAcceptanceAttempt attempt) =>
        $"background-acceptance-{AcceptanceAttemptOutcomeToken(attempt.Outcome)}: {Sanitize(attempt.Detail ?? attempt.AttemptId)}";

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

    private static IReadOnlyList<Goal> OrderParallelAcceptanceEligibleGoals(IReadOnlyList<Goal> eligible) =>
        eligible
            .OrderBy(ParallelAcceptanceVerifiedAt)
            .ThenBy(goal => goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .ThenBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .ToArray();

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
            return !GoalOperationJournal.HasRetiredTerminalDisposition(
                GoalOperationJournal.Read(driver.ExecutionDirectory, goal.Id));
        }
        catch
        {
            return true;
        }
    }

    private static DateTimeOffset ParallelAcceptanceVerifiedAt(Goal goal)
    {
        var lastVerification = goal.Tasks
            .Select(task => task.LastVerification?.CompletedAt)
            .Where(completedAt => completedAt.HasValue)
            .Select(completedAt => completedAt!.Value)
            .DefaultIfEmpty(goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .Max();
        return goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("Verified", StringComparison.OrdinalIgnoreCase))
            .Select(evt => evt.OccurredAt)
            .DefaultIfEmpty(lastVerification)
            .Min();
    }

    private static bool HasCompletedPassedVerificationForAllTasks(Goal goal) =>
        goal.Tasks.Count > 0 &&
        goal.Tasks.All(task =>
            task.Status == WorkTaskStatus.Cancelled ||
            (task.Status == WorkTaskStatus.Completed &&
             task.LastVerification is { Succeeded: true }));

    private static bool ShouldDeferForParallelAcceptanceFairness(string oldestGoalId)
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

    private static bool? TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(Goal goal, ConductorDriver driver)
    {
        try
        {
            return HasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver);
        }
        catch
        {
            return null;
        }
    }

    private static ConductorParallelAcceptanceCandidate? TryBuildParallelAcceptanceCandidate(
        ConductorDriver driver,
        Goal goal,
        ConductorAutonomyPolicy policy,
        int slotIndex,
        out Exception? exception)
    {
        exception = null;
        try
        {
            return driver.TryBuildParallelAcceptanceCandidate(goal, policy, slotIndex);
        }
        catch (Exception ex)
        {
            exception = ex;
            return null;
        }
    }

    private static ConductorAdvanceResult CompleteParallelAcceptanceRun(
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
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
                    $"Background acceptance attempt cancelled; retry on next conduct tick: {Sanitize(cancelled.Message)}");
            }

            if (run.Exception is BuildLockBlockedException buildLock)
            {
                return new ConductorAdvanceResult(
                    run.Candidate.Goal.Id.Value,
                    run.Candidate.GoalPrefix,
                    policy.Name,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Build artifact lock blocked acceptance; retry on next conduct tick. {FormatBuildLockBlocked(buildLock.Attribution)}"));
            }

            return ParallelAcceptanceFault(driver, run.Candidate, policy, run.Exception);
        }

        if (run.EarlyResult is not null)
        {
            return driver.ReplayParallelLandingEarlyOutcome(run.Candidate, policy, run.EarlyResult, run.EarlyOutcome);
        }

        if (run.Acceptance is null)
        {
            return ParallelAcceptanceFault(
                driver,
                run.Candidate,
                policy,
                new InvalidOperationException("Parallel acceptance produced no result."));
        }

        try
        {
            return driver.CompleteParallelLandingAcceptance(run.Candidate, policy, run.Acceptance);
        }
        catch (Exception ex)
        {
            return ParallelAcceptanceFault(driver, run.Candidate, policy, ex);
        }
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

    private static ConductorAdvanceResult ParallelAcceptanceTerminal(
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

        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock)
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
                $"Background acceptance attempt cancelled; retry on next conduct tick. attempt={attempt.AttemptId}: {Sanitize(attempt.Detail ?? "cancelled")}");
        }

        if (ConductorParallelAcceptanceAttemptCoordinator.IsTransientTerminalFailure(attempt) &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Transient background acceptance {AcceptanceAttemptOutcomeToken(attempt.Outcome)} ({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); retry on next conduct tick. attempt={attempt.AttemptId}: {Sanitize(attempt.Detail ?? "transient artifact fault")}");
        }

        return driver.EscalateParallelLandingAcceptance(
            candidate,
            policy,
            $"background acceptance {AcceptanceAttemptOutcomeToken(attempt.Outcome)}: {Sanitize(attempt.Detail ?? attempt.AttemptId)}");
    }

    private static ConductorAdvanceResult ParallelAcceptanceFault(
        ConductorDriver driver,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Exception exception) =>
        driver.EscalateParallelLandingAcceptance(
            candidate,
            policy,
            $"parallel acceptance fault: {Sanitize(exception.Message)}");

    private static string AcceptanceRunDisposition(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return run.Exception switch
            {
                DotnetBuildSlotsBusyException => "slots-busy",
                OperationCanceledException => "cancelled",
                BuildLockBlockedException => "build-lock-blocked",
                _ => "fault"
            };
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.WasEscalated ? "blocked" : "done";
        }

        return run.Acceptance?.Passed == true ? "passed" : "failed";
    }

    private static string AcceptanceAttemptOutcomeToken(ConductorParallelAcceptanceAttemptOutcome outcome) =>
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
            ConductorParallelAcceptanceAttemptOutcome.LaunchFailed => "launch-failed",
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
            ConductorAdvanceOutcome.Held h      => $"GOAL goal={label} result=held state={h.State}{slot}",
            ConductorAdvanceOutcome.Escalated e => $"GOAL goal={label} result=escalated state={e.State}{slot} reason={Sanitize(e.Reason)}",
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
            if (escalatedGoals.Contains(depId.Value))
                return $"dependency escalated: {depId.Value[..8]}";

            if (completedGoals.Contains(depId.Value) ||
                kernel.IsKnownCompletedDependencyGoal(depId) ||
                (kernel.TryGetKnownDependencyGoalStatus(depId, out var dependencyStatus) &&
                 IsMetadataSatisfiedDependencyStatus(dependencyStatus)))
            {
                continue;
            }

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

    private static bool IsMetadataSatisfiedDependencyStatus(string status) =>
        status.Equals(GoalStatus.Completed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("CleanedUp", StringComparison.OrdinalIgnoreCase);

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

            if (state != GoalLifecycleState.CleanedUp)
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
        string? onlyGoalId,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        HashSet<string> escalatedGoals,
        HashSet<string> reapedGoals,
        GoalProjectionCache goalProjectionCache)
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

            var currentFingerprint = BuildEscalatedGoalStateFingerprint(kernel, driver, goal);
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

        if (kernel.GetPendingHumanInput(goal.Id).Count > 0)
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

    private static BatchSetAsideCondition GetSetAsideCondition(ConductorAdvanceResult result) =>
        result.Outcome is ConductorAdvanceOutcome.Escalated { State: GoalLifecycleState.AwaitingClarification }
            ? BatchSetAsideCondition.AwaitingClarification
            : BatchSetAsideCondition.LifecycleEscalation;

    private static void SetAside(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        BatchSetAsideCondition condition,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals) =>
        setAsideGoals[goal.Id.Value] = new BatchSetAsideEntry(
            goal.Id.Value,
            condition,
            BuildEscalatedGoalStateFingerprint(kernel, driver, goal));

    private static string BuildEscalatedGoalStateFingerprint(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal)
    {
        var lifecycleState = TryResolveLifecycleState(driver, goal);
        var attentionCount = kernel.GetPendingHumanInput(goal.Id).Count;
        var taskParts = goal.Tasks
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .Select(task =>
                string.Join(
                    ":",
                    new[]
                    {
                    task.Id.Value,
                    task.Status.ToString(),
                    task.LastDispatch is null ? "dispatch=none" : $"dispatch={task.LastDispatch.DispatchedAt.UtcTicks}:{task.LastDispatch.WorkerName}",
                    task.LastProcess is null ? "process=none" : $"process={task.LastProcess.IsRunning}:{task.LastProcess.CompletedAt?.UtcTicks}:{task.LastProcess.ExitCode}:{task.LastProcess.WasCancelled}",
                    task.LastVerification is null ? "verification=none" : $"verification={task.LastVerification.Succeeded}:{task.LastVerification.ExitCode}:{task.LastVerification.CompletedAt.UtcTicks}",
                    task.LastExecution is null ? "execution=none" : $"execution={task.LastExecution.StopReason}:{task.LastExecution.CompletedAt.UtcTicks}"
                    }));

        return string.Join("|", new[] { goal.Status.ToString(), lifecycleState, $"attention={attentionCount}" }.Concat(taskParts));
    }

    private static string TryResolveLifecycleState(ConductorDriver driver, Goal goal)
    {
        try
        {
            return GoalLifecycle.ResolveState(goal, driver.GetFacts(goal)).ToString();
        }
        catch
        {
            return "LifecycleState=unknown";
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

    private static bool IsPreWalkExcludedGoal(Goal goal) =>
        goal.Status == GoalStatus.Parked || IsPreWalkExcludedTerminalGoal(goal);

    private static bool IsPreWalkExcludedTerminalGoal(Goal goal) =>
        goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed
        || IsStaleTerminalGoalWithAssignedWork(goal);

    private static bool IsLoopEligibleGoal(Goal goal, ConductorDriver driver, GoalProjectionCache goalProjectionCache)
    {
        if (IsPreWalkExcludedGoal(goal))
        {
            return false;
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

    private static bool IsTransientVerificationFailure(ConductorAdvanceResult result) =>
        result.Outcome is ConductorAdvanceOutcome.Escalated esc
        && esc.State == GoalLifecycleState.Verified
        && esc.Reason.Contains("Acceptance verification failed", StringComparison.OrdinalIgnoreCase);

    private static bool HasPersistedVerifiedAcceptanceEscalation(Goal goal)
    {
        foreach (var evt in goal.Timeline.Reverse())
        {
            if (ClearsPersistedVerifiedAcceptanceEscalation(evt))
            {
                return false;
            }

            if (evt.Kind == ProgressKind.GoalPolicyDecision
                && evt.Message.Contains("escalated at Verified", StringComparison.OrdinalIgnoreCase)
                && IsPersistedVerifiedAcceptanceEscalationMessage(evt.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPersistedVerifiedAcceptanceEscalationMessage(string message) =>
        message.Contains("Acceptance verification failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("background acceptance", StringComparison.OrdinalIgnoreCase);

    private static bool HasUnresolvedPersistedVerifiedAcceptanceEscalation(Goal goal, ConductorDriver driver)
    {
        if (!HasPersistedVerifiedAcceptanceEscalation(goal))
        {
            return false;
        }

        try
        {
            return GoalLifecycle.ResolveState(goal, driver.GetFacts(goal)) != GoalLifecycleState.CleanedUp;
        }
        catch
        {
            return true;
        }
    }

    private static bool ClearsPersistedVerifiedAcceptanceEscalation(ProgressEvent evt) =>
        evt.Kind is ProgressKind.TaskRetried or ProgressKind.GoalCancelled or ProgressKind.GoalSuperseded
        || (evt.Kind is ProgressKind.HumanInputRequested or ProgressKind.GoalPolicyDecision
            && evt.Message.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase))
        || evt.Kind == ProgressKind.HumanInputReceived;

    private static bool IsStopRequested(string stopFilePath) =>
        !string.IsNullOrEmpty(stopFilePath) && File.Exists(stopFilePath);

    private static TimeSpan GetWatchFallbackInterval(
        AgentOrchestratorKernel kernel,
        string? onlyGoalId,
        TimeSpan idleInterval) =>
        HasRunningDispatch(kernel, onlyGoalId)
            ? TimeSpan.FromSeconds(WatchStopPollIntervalSeconds)
            : idleInterval;

    private static bool HasRunningDispatch(AgentOrchestratorKernel kernel, string? onlyGoalId) =>
        kernel.Goals.Any(goal =>
            (onlyGoalId is null || goal.Id.Value == onlyGoalId)
            && goal.Tasks.Any(task => task.LastProcess is { IsRunning: true }));

    private static int CountRunningDispatches(AgentOrchestratorKernel kernel, string? onlyGoalId) =>
        kernel.Goals
            .Where(goal => onlyGoalId is null || goal.Id.Value == onlyGoalId)
            .Sum(goal => goal.Tasks.Count(task => task.LastProcess is { IsRunning: true }));

    private static IReadOnlyList<string> GetRunningDispatchExitCodePaths(AgentOrchestratorKernel kernel, string? onlyGoalId) =>
        kernel.Goals
            .Where(goal => onlyGoalId is null || goal.Id.Value == onlyGoalId)
            .SelectMany(goal => goal.Tasks)
            .Select(task => task.LastProcess)
            .OfType<TaskProcessRecord>()
            .Where(process => process.IsRunning && !string.IsNullOrWhiteSpace(process.ExitCodePath))
            .Select(process => process.ExitCodePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static WatchSleepResult SleepUntilNextTick(
        TimeSpan interval,
        string stopFilePath,
        IConductorWakeSignal? wakeSignal,
        IReadOnlyList<string> trackedExitCodePaths)
    {
        wakeSignal?.UpdateTrackedExitArtifacts(trackedExitCodePaths);
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

internal enum BatchSetAsideCondition
{
    AwaitingClarification,
    DependencyEscalated,
    AdvanceFault,
    LifecycleEscalation
}

internal enum WatchSleepResult
{
    FallbackElapsed,
    StopRequested,
    WakeSignaled
}

internal sealed record BatchSetAsideEntry(string GoalId, BatchSetAsideCondition Condition, string StateFingerprint);

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

    private static string BuildFingerprint(Goal goal)
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
    ConductorLoopHandoffResult? Handoff = null);

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
