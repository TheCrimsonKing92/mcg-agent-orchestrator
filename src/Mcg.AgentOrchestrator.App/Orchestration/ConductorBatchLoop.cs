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

    private readonly Action<AgentOrchestratorKernel> _sweep;
    private readonly Action<AgentOrchestratorKernel, Goal> _reapGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel, Goal> _detachGoalRunningDispatches;
    private readonly Action<AgentOrchestratorKernel> _recoverInterruptedDispatches;
    private readonly ConductorWatchProgressReporter _watchProgressReporter;

    public ConductorBatchLoop(
        Action<AgentOrchestratorKernel>? sweep = null,
        Action<AgentOrchestratorKernel, Goal>? reapGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel, Goal>? detachGoalRunningDispatches = null,
        Action<AgentOrchestratorKernel>? recoverInterruptedDispatches = null,
        ConductorWatchProgressReporter? watchProgressReporter = null)
    {
        _sweep = sweep ?? (_ => { });
        _reapGoalRunningDispatches = reapGoalRunningDispatches ?? ((_, _) => { });
        _detachGoalRunningDispatches = detachGoalRunningDispatches ?? _reapGoalRunningDispatches;
        _recoverInterruptedDispatches = recoverInterruptedDispatches ?? (_ => { });
        _watchProgressReporter = watchProgressReporter ?? new ConductorWatchProgressReporter();
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
        Action<TimeSpan>? busyWriteDelay = null)
    {
        var excludedGoals = new HashSet<string>(StringComparer.Ordinal);
        var setAsideGoals = new Dictionary<string, BatchSetAsideEntry>(StringComparer.Ordinal);
        var completedGoals = new HashSet<string>(StringComparer.Ordinal);
        var escalatedGoals = new HashSet<string>(StringComparer.Ordinal);
        var reapedGoals = new HashSet<string>(StringComparer.Ordinal);
        var retryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastGoalDisposition = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalTicks = 0;
        var totalAdvanced = 0;
        var totalHeld = 0;
        var totalEscalated = 0;
        var totalRetried = 0;
        var stopRequested = false;
        var started = DateTimeOffset.UtcNow;

        while (true)
        {
            if (IsStopRequested(stopFilePath))
            {
                stopRequested = true;
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-file");
                Console.WriteLine($"[conduct --loop] Stop signal detected at tick {totalTicks + 1}; no new dispatches will be started.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "stop", null, busyWriteDelay);
                break;
            }

            if (maxIterations.HasValue && totalTicks >= maxIterations.Value)
            {
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=max-iter max={maxIterations.Value}");
                Console.WriteLine($"[conduct --loop] Max iterations ({maxIterations.Value}) reached after {totalTicks} ticks.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "max-iterations", null, busyWriteDelay);
                break;
            }

            if (maxDuration.HasValue && DateTimeOffset.UtcNow - started >= maxDuration.Value)
            {
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=max-duration seconds={(int)maxDuration.Value.TotalSeconds}");
                Console.WriteLine($"[conduct --loop] Max duration ({maxDuration.Value.TotalSeconds:0}s) reached after {totalTicks} ticks.");
                DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "max-duration", null, busyWriteDelay);
                break;
            }

            _sweep(kernel);
            _recoverInterruptedDispatches(kernel);
            ReadmitResolvedSetAsideGoals(kernel, driver, onlyGoalId, setAsideGoals, escalatedGoals, reapedGoals);
            MarkCompletedDependencyGoals(kernel, driver, onlyGoalId, completedGoals);

            var parkedExcludedCount = CountParkedExcludedGoals(kernel, onlyGoalId, excludedGoals);
            var eligible = kernel.Goals
                .Where(g => (onlyGoalId is null || g.Id.Value == onlyGoalId)
                    && !excludedGoals.Contains(g.Id.Value)
                    && !setAsideGoals.ContainsKey(g.Id.Value)
                    && g.Status != GoalStatus.Parked
                    && IsLoopEligibleGoal(g, driver))
                .ToArray();

            if (eligible.Length == 0)
            {
                // Daemon keep-alive: when configured (and watching), an empty backlog is NOT a reason to
                // exit — sleep and keep polling so goals submitted later are ingested by the sweep and
                // driven. A one-shot `conduct --loop` (keepAliveWhenIdle=false) still completes here.
                if (keepAliveWhenIdle && watchInterval is not null)
                {
                    var idleInterval = GetWatchFallbackInterval(kernel, onlyGoalId, watchInterval.Value);
                    EmitProgress($"IDLE_SLEEP seconds={(int)idleInterval.TotalSeconds}");
                    var idleSleep = sleepFunc is not null
                        ? (sleepFunc(idleInterval) ? WatchSleepResult.StopRequested : WatchSleepResult.FallbackElapsed)
                        : SleepUntilNextTick(idleInterval, stopFilePath, wakeSignal);
                    if (idleSleep == WatchSleepResult.WakeSignaled)
                    {
                        _sweep(kernel);
                        _recoverInterruptedDispatches(kernel);
                        TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "idle-wake-sweep", null, busyWriteDelay);
                    }

                    if (idleSleep == WatchSleepResult.StopRequested || IsStopRequested(stopFilePath))
                    {
                        stopRequested = true;
                        EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-while-idle");
                        DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                        TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "stop-while-idle", null, busyWriteDelay);
                        break;
                    }

                    continue;
                }

                EmitProgress($"LOOP_STOP tick={totalTicks} reason=all-done-or-escalated");
                Console.WriteLine($"[conduct --loop] All goals done or escalated; loop complete after {totalTicks} ticks.");
                break;
            }

            totalTicks++;
            var changedGoalLines = new List<string>();
            var changedGoalIds = new HashSet<GoalId>();

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
                changedGoalLines);

            foreach (var goal in eligible)
            {
                var label = goal.Id.Value[..8];

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
                        result = driver.AdvanceOnce(goal, policy);
                    }
                    catch (Exception ex)
                    {
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
                    }
                }

                var goalProgressLine = FormatGoalProgressLine(
                    label,
                    result.Outcome,
                    serialRetryRan ? null : parallelLandingOutcome?.SlotIndex);
                if (RecordChangedDisposition(goal.Id.Value, goalProgressLine, lastGoalDisposition, changedGoalLines))
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
            }

            totalAdvanced  += tickAdvanced;
            totalHeld      += tickHeld;
            totalEscalated += tickEscalated;
            totalRetried   += tickRetried;

            var emitTickSummary = changedGoalLines.Count > 0
                || parkedExcludedCount > 0
                || totalTicks % QuietSummaryEveryTicks == 0;
            var tickLines = new List<string>();
            if (watchInterval is not null)
            {
                foreach (var goal in eligible)
                {
                    foreach (var line in _watchProgressReporter.BuildLines(goal, quiet, watchInterval, stallWarningThreshold))
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
                    TryPersistGoalTick(persistGoalTick, kernel, changedGoalIds.ToArray(), totalTicks, tickLines, busyWriteDelay);
            }
            else
            {
                TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(changedGoalIds, onlyGoalId), "tick", tickLines, busyWriteDelay);
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
                    TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "no-progress", tickLines, busyWriteDelay);
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
                    : SleepUntilNextTick(fallbackInterval, stopFilePath, wakeSignal);

                if (sleepResult == WatchSleepResult.WakeSignaled)
                {
                    _sweep(kernel);
                    _recoverInterruptedDispatches(kernel);
                    TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "wake-sweep", tickLines, busyWriteDelay);
                }

                if (sleepResult == WatchSleepResult.StopRequested || IsStopRequested(stopFilePath))
                {
                    stopRequested = true;
                    EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-file-during-sleep");
                    Console.WriteLine($"[conduct --loop --watch] Stop signal detected during sleep after tick {totalTicks}; no new dispatches.");
                    DetachNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    TryPersistTick(persistTick, kernel, totalTicks, ResolveGoalContext(kernel, onlyGoalId), "stop-during-sleep", tickLines, busyWriteDelay);
                    break;
                }

                continue;
            }

            onTick?.Invoke(tickSummary);
        }

        return new BatchLoopSummary(totalTicks, totalAdvanced, totalHeld, totalEscalated, totalRetried, stopRequested);
    }

    // Emit a compact progress line to stdout with immediate flush; optionally accumulate in a list.
    private static void EmitProgress(string line, List<string>? accumulator = null)
    {
        Console.WriteLine(line);
        Console.Out.Flush();
        accumulator?.Add(line);
    }

    private static bool TryPersistGoalTick(
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persistGoalTick,
        AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> changedGoalIds,
        int tick,
        List<string> tickLines,
        Action<TimeSpan>? busyWriteDelay)
    {
        var goals = ResolveGoalContext(changedGoalIds, onlyGoalId: null);
        return TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, changedGoalIds),
            tick,
            goals,
            "goal",
            tickLines,
            busyWriteDelay);
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
        List<string> changedGoalLines)
    {
        if (lastGoalDisposition.TryGetValue(goalId, out var previous)
            && string.Equals(previous, progressLine, StringComparison.Ordinal))
        {
            return false;
        }

        lastGoalDisposition[goalId] = progressLine;
        changedGoalLines.Add(progressLine);
        return true;
    }

    // Sanitize a detail string for compact line format (no spaces, max 40 chars).
    private static string Sanitize(string value)
    {
        var s = value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
        return s.Length > 40 ? s[..40] : s;
    }

    private static IReadOnlyDictionary<string, ParallelLandingOutcome> RunParallelAcceptanceBatch(
        IReadOnlyList<Goal> eligible,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        int tick,
        List<string> changedGoalLines)
    {
        var slotCount = Math.Max(0, DotnetBuildEnvironmentManager.StableSlotCount - 1);
        if (slotCount < 2)
        {
            return new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        }

        var candidates = new List<ConductorParallelAcceptanceCandidate>();
        foreach (var goal in eligible)
        {
            if (candidates.Count >= slotCount)
            {
                break;
            }

            if (GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel) is not null ||
                TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver) != false)
            {
                continue;
            }

            var candidate = TryBuildParallelAcceptanceCandidate(driver, goal, policy, candidates.Count);
            if (candidate is null || candidates.Any(existing => existing.Overlaps(candidate)))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        if (candidates.Count < 2)
        {
            return new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        }

        foreach (var candidate in candidates)
        {
            EmitProgress(
                $"ACCEPTANCE goal={candidate.GoalPrefix} slot=slot-{candidate.SlotIndex} result=started tick={tick}",
                changedGoalLines);
        }

        var tasks = candidates
            .Select(candidate => Task.Run(() => driver.RunParallelLandingAcceptance(candidate, policy)))
            .ToArray();
        Task.WaitAll(tasks);

        var results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        foreach (var run in tasks.Select(task => task.Result)
                     .OrderBy(result => result.Candidate.SlotIndex))
        {
            var result = CompleteParallelAcceptanceRun(driver, policy, run);
            results[run.Candidate.Goal.Id.Value] = new ParallelLandingOutcome(result, run.Candidate.SlotIndex);
            EmitProgress(
                $"ACCEPTANCE goal={run.Candidate.GoalPrefix} slot=slot-{run.Candidate.SlotIndex} result={AcceptanceRunDisposition(run)} tick={tick}",
                changedGoalLines);
        }

        return results;
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
        int slotIndex)
    {
        try
        {
            return driver.TryBuildParallelAcceptanceCandidate(goal, policy, slotIndex);
        }
        catch
        {
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
            return ParallelAcceptanceFault(run.Candidate, policy, run.Exception);
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult;
        }

        if (run.Acceptance is null)
        {
            return ParallelAcceptanceFault(
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
            return ParallelAcceptanceFault(run.Candidate, policy, ex);
        }
    }

    private static ConductorAdvanceResult ParallelAcceptanceFault(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Exception exception) =>
        new(
            candidate.Goal.Id.Value,
            candidate.GoalPrefix,
            policy.Name,
            new ConductorAdvanceOutcome.Escalated(
                GoalLifecycleState.Verified,
                $"parallel acceptance fault: {Sanitize(exception.Message)}"));

    private static string AcceptanceRunDisposition(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return "fault";
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.WasEscalated ? "blocked" : "done";
        }

        return run.Acceptance?.Passed == true ? "passed" : "failed";
    }

    private static string FormatGoalProgressLine(string label, ConductorAdvanceOutcome outcome, int? slotIndex = null)
    {
        var slot = slotIndex.HasValue ? $" slot=slot-{slotIndex.Value}" : string.Empty;
        return outcome switch
        {
            ConductorAdvanceOutcome.Executed e  => $"GOAL goal={label} result=executed state={e.FromState}{slot}",
            ConductorAdvanceOutcome.Held h      => $"GOAL goal={label} result=held state={h.State}{slot}",
            ConductorAdvanceOutcome.Escalated e => $"GOAL goal={label} result=escalated state={e.State}{slot}",
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

            if (!completedGoals.Contains(depId.Value) && !kernel.IsKnownCompletedDependencyGoal(depId))
            {
                var depPrefix = kernel.Goals.FirstOrDefault(g => g.Id == depId)?.Id.Value[..8] ?? depId.Value[..8];
                return $"waiting on dependency {depPrefix}";
            }
        }

        return null;
    }

    private static void MarkCompletedDependencyGoals(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        string? onlyGoalId,
        HashSet<string> completedGoals)
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

            GoalLifecycleFacts facts;
            try
            {
                facts = driver.GetFacts(goal);
            }
            catch
            {
                continue;
            }

            if (GoalLifecycle.ResolveState(goal, facts) != GoalLifecycleState.CleanedUp)
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
        HashSet<string> reapedGoals)
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

            setAsideGoals.Remove(entry.GoalId);
            escalatedGoals.Remove(entry.GoalId);
            reapedGoals.Remove(entry.GoalId);
            kernel.RecordGoalPolicyDecision(
                goal.Id,
                $"Batch loop re-admitted escalated goal after state changed ({entry.Condition}).");
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

    private static bool IsLoopEligibleGoal(Goal goal, ConductorDriver driver)
    {
        if (IsStaleTerminalGoalWithAssignedWork(goal))
        {
            return false;
        }

        if (goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded)
        {
            return false;
        }

        if (goal.Status is GoalStatus.Verified or GoalStatus.Completed)
        {
            try
            {
                var facts = driver.GetFacts(goal);
                var state = GoalLifecycle.ResolveState(goal, facts);
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
                && evt.Message.Contains("Acceptance verification failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

    private static WatchSleepResult SleepUntilNextTick(TimeSpan interval, string stopFilePath, IConductorWakeSignal? wakeSignal)
    {
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

internal sealed record ParallelLandingOutcome(ConductorAdvanceResult Result, int SlotIndex);

public sealed record BatchLoopSummary(
    int Ticks,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    bool StopRequested);

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
