using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Interprets evidence that a remembered condition changed. The loop owns set-aside
// membership; ordinary admission and gates still own permission to execute or land.
internal static class SetAsideGoalReadmission
{
    internal sealed record Decision(string Message, BatchSetAsideEntry? AcknowledgedEntry = null);

    internal static Decision? Evaluate(BatchSetAsideEntry entry, Goal goal, ConductorDriver driver)
    {
        if (entry.Condition == BatchSetAsideCondition.LifecycleEscalation &&
            entry.AcceptanceEscalation is { } acceptanceEscalation &&
            goal.Status != GoalStatus.Parked &&
            VerifiedAcceptanceEscalationDecision.FindAcknowledgement(goal, acceptanceEscalation) is { } acknowledgement &&
            acknowledgement != entry.LastAcceptanceAcknowledgement)
        {
            try
            {
                // The unresolved predicate short-circuits after a clearing event; it
                // cannot alone prove current lifecycle facts were successfully read.
                var facts = driver.GetFacts(goal);
                if (facts is null) return null;
                _ = GoalLifecycle.ResolveState(goal, facts);
                if (VerifiedAcceptanceEscalationDecision.TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver) != false)
                    return null;
            }
            catch
            {
                return null;
            }

            return new Decision(
                $"Batch loop re-admitted acknowledged acceptance escalation: " +
                $"escalation={acceptanceEscalation.TimelineIndex}:{acceptanceEscalation.ContentHash}; " +
                $"acknowledgement={acknowledgement.TimelineIndex}:{acknowledgement.ContentHash}.",
                entry with { LastAcceptanceAcknowledgement = acknowledgement });
        }

        var currentFingerprint = entry.Condition == BatchSetAsideCondition.AwaitingClarification
            ? TryResolveLifecycleState(driver, goal) switch
            {
                "LifecycleState=unknown" => entry.StateFingerprint,
                nameof(GoalLifecycleState.AwaitingClarification) => BuildEscalatedGoalStateFingerprint(goal),
                var state => $"clarification={state}"
            }
            : BuildEscalatedGoalStateFingerprint(goal);
        return string.Equals(currentFingerprint, entry.StateFingerprint, StringComparison.Ordinal)
            ? null
            : new Decision($"Batch loop re-admitted escalated goal after state changed ({entry.Condition}).");
    }

    internal static string BuildEscalatedGoalStateFingerprint(Goal goal)
    {
        var taskParts = goal.Tasks
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .Select(task =>
                string.Join(
                    ":",
                    new[]
                    {
                        task.Id.Value,
                        $"candidate={task.LastDispatch?.ResultCommit?.Trim() ?? task.LastVerification?.ReviewedCommit?.Trim() ?? "none"}",
                        task.Status.ToString(),
                        task.LastDispatch is null ? "dispatch=none" : $"dispatch={task.LastDispatch.DispatchedAt.UtcTicks}:{task.LastDispatch.WorkerName}",
                        task.LatestRetryAt is null ? "retry=none" : $"retry={task.LatestRetryAt.Value.UtcTicks}"
                    }));

        return string.Join("|", taskParts);
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
}
