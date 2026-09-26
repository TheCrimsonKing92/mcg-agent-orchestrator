namespace Mcg.AgentOrchestrator.Core;

public static class UnchangedCandidateVerdict
{
    public const string Rejected = "rejected";

    public static string Derive(Goal goal, TaskSpec task, TaskVerificationRecord verification)
    {
        var workerVerdict = WorkerResultBlockers.TryFindNeedsWorkVerdict(verification, out _)
            ? "needs-work"
            : WorkerResultBlockers.TryFindBlockedAtCapVerdict(verification, out _)
                ? "blocked-at-cap"
                : verification.AssignedScopeComplete is false
                    ? "revision-requested"
                    : verification.Succeeded ? "passed" : "failed";
        return workerVerdict == "passed" && IsConductorRejected(goal, task, verification)
            ? Rejected : workerVerdict;
    }

    private static bool IsConductorRejected(Goal goal, TaskSpec task, TaskVerificationRecord verification)
    {
        // Match the verification's ordinal, not its timestamp: FakeClock can give a
        // verification and its rejection the same instant, even across retries.
        var verificationIndex = -1;
        for (var index = 0; index < task.VerificationHistory.Count; index++)
        {
            if (ReferenceEquals(task.VerificationHistory[index], verification))
            {
                verificationIndex = index;
                break;
            }
        }
        if (verificationIndex < 0) return false;

        // Old verification records can be pruned from the task's capped history.
        // The latest retained records still align with timeline events from the end.
        var remainingFromEnd = task.VerificationHistory.Count - 1 - verificationIndex;
        var start = -1;
        for (var index = goal.Timeline.Count - 1; index >= 0; index--)
        {
            var evt = goal.Timeline[index];
            if (evt.TaskId != task.Id) continue;
            if (evt.Kind == ProgressKind.TaskVerificationRecorded)
            {
                if (remainingFromEnd-- == 0) { start = index; break; }
            }
        }
        if (start < 0) return false;
        for (var index = start + 1; index < goal.Timeline.Count; index++)
        {
            var evt = goal.Timeline[index];
            if (evt.TaskId != task.Id) continue;
            if (evt.Kind == ProgressKind.TaskDispatchRecorded) return false;
            if (evt.Kind == ProgressKind.TaskFailed) return true;
            if (evt.Kind == ProgressKind.TaskVerificationRecorded) return false;
        }
        return false;
    }
}
