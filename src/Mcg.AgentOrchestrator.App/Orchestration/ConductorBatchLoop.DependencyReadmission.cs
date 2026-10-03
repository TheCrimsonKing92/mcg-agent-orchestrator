using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
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
}
