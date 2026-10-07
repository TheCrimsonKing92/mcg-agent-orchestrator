using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // Emit a compact progress line to stdout with immediate flush; optionally accumulate in a list.
    internal static void EmitProgress(string line, List<string>? accumulator = null)
    {
        var stampedLine = $"{line} ts={DiagnosticUtcNow():O}";
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
            "blocked-recheck-heartbeat" or "policy-reload-failed" or "prompt-rollout-suspect" ||
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
            "GOAL_LEFT_WORKING_SET" => "goal-left-working-set",
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
            "ACCEPTANCE_COHORT_ATTRIBUTION_VERDICT" => "acceptance-cohort",
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
            "PROMPT_ROLLOUT_SUSPECT" => "prompt-rollout-suspect", "CONSOLE_CODE_PAGE_CHANGED" => "console-code-page-changed",
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
            .SelectMany(goal => goal.Blockers
                .Where(blocker => !ReconcileSweepRemediationCoordinator.IsAwaitingConductorAcceptanceGate(blocker))
                .Select(blocker => new
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

    private static string? TryExtractToken(string line, string prefix)
    {
        var start = line.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += prefix.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static string FormatPhaseTiming(int tick, string phase, TimeSpan elapsed, string detail, long? cpuMs = null) =>
        $"PHASE_TIMING tick={tick} phase={phase} elapsed_ms={(long)elapsed.TotalMilliseconds} {detail}{(cpuMs is { } cpu ? $" cpu_ms={cpu}" : string.Empty)}";

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
}
