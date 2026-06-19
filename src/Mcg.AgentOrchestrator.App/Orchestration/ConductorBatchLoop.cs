using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorBatchLoop
{
    internal const string StopFileName = ".conduct-stop";
    internal const int DefaultMaxVerifyRetries = 2;
    internal const int DefaultWatchIntervalSeconds = 15;
    internal const int WatchStopPollIntervalSeconds = 5;

    private readonly Action<AgentOrchestratorKernel> _sweep;
    private readonly Action<AgentOrchestratorKernel, Goal> _reapGoalRunningDispatches;

    public ConductorBatchLoop(
        Action<AgentOrchestratorKernel>? sweep = null,
        Action<AgentOrchestratorKernel, Goal>? reapGoalRunningDispatches = null)
    {
        _sweep = sweep ?? (_ => { });
        _reapGoalRunningDispatches = reapGoalRunningDispatches ?? ((_, _) => { });
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
        TimeSpan? maxDuration = null,
        string? onlyGoalId = null,
        Action<AgentOrchestratorKernel>? persistTick = null,
        bool keepAliveWhenIdle = false)
    {
        var excludedGoals = new HashSet<string>(StringComparer.Ordinal);
        var completedGoals = new HashSet<string>(StringComparer.Ordinal);
        var escalatedGoals = new HashSet<string>(StringComparer.Ordinal);
        var reapedGoals = new HashSet<string>(StringComparer.Ordinal);
        var retryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
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
                ReapNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                persistTick?.Invoke(kernel);
                break;
            }

            if (maxIterations.HasValue && totalTicks >= maxIterations.Value)
            {
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=max-iter max={maxIterations.Value}");
                Console.WriteLine($"[conduct --loop] Max iterations ({maxIterations.Value}) reached after {totalTicks} ticks.");
                ReapNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                persistTick?.Invoke(kernel);
                break;
            }

            if (maxDuration.HasValue && DateTimeOffset.UtcNow - started >= maxDuration.Value)
            {
                EmitProgress($"LOOP_STOP tick={totalTicks} reason=max-duration seconds={(int)maxDuration.Value.TotalSeconds}");
                Console.WriteLine($"[conduct --loop] Max duration ({maxDuration.Value.TotalSeconds:0}s) reached after {totalTicks} ticks.");
                ReapNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                persistTick?.Invoke(kernel);
                break;
            }

            _sweep(kernel);

            var eligible = kernel.Goals
                .Where(g => (onlyGoalId is null || g.Id.Value == onlyGoalId)
                    && !excludedGoals.Contains(g.Id.Value)
                    && g.Status is not GoalStatus.Cancelled
                    && g.Status is not GoalStatus.Superseded)
                .ToArray();

            if (eligible.Length == 0)
            {
                // Daemon keep-alive: when configured (and watching), an empty backlog is NOT a reason to
                // exit — sleep and keep polling so goals submitted later are ingested by the sweep and
                // driven. A one-shot `conduct --loop` (keepAliveWhenIdle=false) still completes here.
                if (keepAliveWhenIdle && watchInterval is not null)
                {
                    EmitProgress($"IDLE_SLEEP seconds={(int)watchInterval.Value.TotalSeconds}");
                    var idleStop = sleepFunc is not null
                        ? sleepFunc(watchInterval.Value)
                        : SleepWithStopCheck(watchInterval.Value, stopFilePath);
                    if (idleStop)
                    {
                        stopRequested = true;
                        EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-while-idle");
                        ReapNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                        persistTick?.Invoke(kernel);
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
            EmitProgress($"TICK tick={totalTicks} eligible={eligible.Length}", tickLines);

            var tickAdvanced = 0;
            var tickHeld = 0;
            var tickEscalated = 0;
            var tickRetried = 0;
            var tickDone = 0;

            foreach (var goal in eligible)
            {
                var label = goal.Id.Value[..8];

                // Dependency gate: check all DependsOn goals before advancing.
                var depHoldReason = GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel);
                if (depHoldReason is not null)
                {
                    EmitProgress($"GOAL goal={label} result=held reason={Sanitize(depHoldReason)}", tickLines);
                    Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → held: {depHoldReason}");
                    kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: held: {depHoldReason}");
                    // A goal held due to a failed/escalated dependency will never unblock; exclude it.
                    if (depHoldReason.StartsWith("dependency escalated", StringComparison.Ordinal))
                    {
                        escalatedGoals.Add(goal.Id.Value);
                        excludedGoals.Add(goal.Id.Value);
                        ReapGoalOnce(kernel, goal, reapedGoals);
                        tickEscalated++;
                    }
                    else
                    {
                        tickHeld++;
                    }

                    continue;
                }

                ConductorAdvanceResult result;
                try
                {
                    result = driver.AdvanceOnce(goal, policy);
                }
                catch (Exception ex)
                {
                    var msg = $"Batch loop tick {totalTicks}: fault isolating goal — advance threw: {Sanitize(ex.Message)}";
                    EmitProgress($"GOAL goal={label} result=escalated reason={Sanitize(ex.Message)}", tickLines);
                    Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → escalated (advance threw): {ex.Message}");
                    kernel.RecordGoalPolicyDecision(goal.Id, msg);
                    escalatedGoals.Add(goal.Id.Value);
                    excludedGoals.Add(goal.Id.Value);
                    ReapGoalOnce(kernel, goal, reapedGoals);
                    tickEscalated++;
                    continue;
                }

                // Auto-retry transient acceptance verification failures (up to maxVerifyRetries re-verifications)
                if (result.WasEscalated && IsTransientVerificationFailure(result))
                {
                    retryCounts.TryGetValue(goal.Id.Value, out var retries);
                    while (retries < maxVerifyRetries && result.WasEscalated && IsTransientVerificationFailure(result))
                    {
                        retries++;
                        retryCounts[goal.Id.Value] = retries;
                        tickRetried++;
                        EmitProgress($"GOAL goal={label} result=retry attempt={retries}/{maxVerifyRetries}", tickLines);
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {goal.Id.Value[..8]} acceptance flake (retry {retries}/{maxVerifyRetries})");
                        kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop auto-retry acceptance verification (attempt {retries}/{maxVerifyRetries})");
                        result = driver.AdvanceOnce(goal, policy);
                    }
                }

                EmitProgress(FormatGoalProgressLine(label, result.Outcome), tickLines);
                Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → {FormatOutcome(result.Outcome)}");
                kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {FormatOutcome(result.Outcome)}");

                if (result.WasExecuted)        { tickAdvanced++; }
                else if (result.IsHeld)        { tickHeld++; }
                else if (result.WasEscalated)  { tickEscalated++; escalatedGoals.Add(goal.Id.Value); excludedGoals.Add(goal.Id.Value); ReapGoalOnce(kernel, goal, reapedGoals); }
                else if (result.IsDone)        { tickDone++;      completedGoals.Add(goal.Id.Value); excludedGoals.Add(goal.Id.Value); }
            }

            totalAdvanced  += tickAdvanced;
            totalHeld      += tickHeld;
            totalEscalated += tickEscalated;
            totalRetried   += tickRetried;

            EmitProgress($"TICK_END tick={totalTicks} advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} done={tickDone}", tickLines);
            Console.WriteLine($"[conduct --loop] Tick {totalTicks} summary: advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} retried={tickRetried} done={tickDone}");

            // Durably checkpoint this tick's progress (dispatches started, reconcile results, escalations).
            // Without this the loop's mutations live only in memory until the whole command returns, so a
            // long-running watch loop never persists and a killed loop loses every dispatch on rollback —
            // the goal then re-dispatches the same stage forever and can never advance.
            persistTick?.Invoke(kernel);

            var tickSummary = new BatchTickSummary(totalTicks, tickAdvanced, tickHeld, tickEscalated, tickRetried, tickDone, WatchSleeping: false)
            {
                ProgressLines = tickLines
            };
            if (tickAdvanced == 0 && tickDone == 0)
            {
                if (watchInterval is null)
                {
                    EmitProgress($"LOOP_STOP tick={totalTicks} reason=no-progress-no-watch");
                    Console.WriteLine($"[conduct --loop] No progress in tick {totalTicks}; all eligible goals held or escalated.");
                    onTick?.Invoke(tickSummary);
                    ReapNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    persistTick?.Invoke(kernel);
                    break;
                }

                var sleepSeconds = (int)watchInterval.Value.TotalSeconds;
                EmitProgress($"WATCH_SLEEP tick={totalTicks} seconds={sleepSeconds}");
                Console.WriteLine($"[conduct --loop --watch] No progress in tick {totalTicks}; sleeping {sleepSeconds}s for workers to complete.");
                onTick?.Invoke(tickSummary with { WatchSleeping = true });

                var stopDuringSleep = sleepFunc is not null
                    ? sleepFunc(watchInterval.Value)
                    : SleepWithStopCheck(watchInterval.Value, stopFilePath);

                if (stopDuringSleep)
                {
                    stopRequested = true;
                    EmitProgress($"LOOP_STOP tick={totalTicks} reason=stop-file-during-sleep");
                    Console.WriteLine($"[conduct --loop --watch] Stop signal detected during sleep after tick {totalTicks}; no new dispatches.");
                    ReapNonTerminalEligibleGoals(kernel, onlyGoalId, excludedGoals, reapedGoals);
                    persistTick?.Invoke(kernel);
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

    // Sanitize a detail string for compact line format (no spaces, max 40 chars).
    private static string Sanitize(string value)
    {
        var s = value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
        return s.Length > 40 ? s[..40] : s;
    }

    private static string FormatGoalProgressLine(string label, ConductorAdvanceOutcome outcome) => outcome switch
    {
        ConductorAdvanceOutcome.Executed e  => $"GOAL goal={label} result=executed state={e.FromState}",
        ConductorAdvanceOutcome.Held h      => $"GOAL goal={label} result=held state={h.State}",
        ConductorAdvanceOutcome.Escalated e => $"GOAL goal={label} result=escalated state={e.State}",
        ConductorAdvanceOutcome.Done d      => $"GOAL goal={label} result=done state={d.State}",
        _                                   => $"GOAL goal={label} result=unknown"
    };

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

            if (!completedGoals.Contains(depId.Value))
            {
                var depPrefix = kernel.Goals.FirstOrDefault(g => g.Id == depId)?.Id.Value[..8] ?? depId.Value[..8];
                return $"waiting on dependency {depPrefix}";
            }
        }

        return null;
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

    private void ReapGoalOnce(AgentOrchestratorKernel kernel, Goal goal, HashSet<string> reapedGoals)
    {
        if (!reapedGoals.Add(goal.Id.Value))
        {
            return;
        }

        _reapGoalRunningDispatches(kernel, goal);
    }

    private static bool IsTerminalGoal(Goal goal) =>
        goal.Status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static bool IsTransientVerificationFailure(ConductorAdvanceResult result) =>
        result.Outcome is ConductorAdvanceOutcome.Escalated esc
        && esc.State == GoalLifecycleState.Verified
        && esc.Reason.Contains("Acceptance verification failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsStopRequested(string stopFilePath) =>
        !string.IsNullOrEmpty(stopFilePath) && File.Exists(stopFilePath);

    // Returns true if the stop file appeared during sleep, false if sleep completed normally.
    private static bool SleepWithStopCheck(TimeSpan interval, string stopFilePath)
    {
        var remaining = interval;
        var poll = TimeSpan.FromSeconds(WatchStopPollIntervalSeconds);
        while (remaining > TimeSpan.Zero)
        {
            if (IsStopRequested(stopFilePath))
                return true;
            var slice = remaining < poll ? remaining : poll;
            Thread.Sleep(slice);
            remaining -= slice;
        }

        return IsStopRequested(stopFilePath);
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
}
