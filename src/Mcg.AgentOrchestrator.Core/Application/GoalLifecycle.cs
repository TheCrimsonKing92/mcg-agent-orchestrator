namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalLifecycleFacts(
    bool WorkspaceExists = false,
    bool IsBlocked = false,
    bool IsMerged = false,
    bool IsRecorded = false,
    bool IsCleanedUp = false,
    bool HasOpenClarification = false)
{
    public static GoalLifecycleFacts None { get; } = new();
}

public static class GoalLifecycle
{
    /// <summary>
    /// Maps a goal to its current canonical lifecycle state using only goal domain state
    /// and externally-supplied workspace/dispatch facts. Pure — no side effects, no I/O.
    /// </summary>
    public static GoalLifecycleState ResolveState(Goal goal, GoalLifecycleFacts? facts = null)
    {
        facts ??= GoalLifecycleFacts.None;

        // Draft = goal created, not yet activated
        if (goal.Status == GoalStatus.Draft)
            return GoalLifecycleState.Created;

        // Terminal goal-level statuses map to Failed
        if (goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed)
            return GoalLifecycleState.Failed;

        // Waiting for human input takes priority over progress states
        if (goal.Status == GoalStatus.WaitingForHuman)
            return GoalLifecycleState.AwaitingHumanInput;

        // Goal is holding for spec clarification (raised Clarification items not yet resolved)
        if (facts.HasOpenClarification)
            return GoalLifecycleState.AwaitingClarification;

        // Operator-set external block (preflight fail, dirty worktree, etc.)
        if (facts.IsBlocked)
            return GoalLifecycleState.Blocked;

        if (goal.Status == GoalStatus.Verifying)
            return GoalLifecycleState.Verifying;

        if (goal.Status == GoalStatus.AcceptanceFailed)
            return GoalLifecycleState.AcceptanceFailed;

        // Task-level failure: goal stays Active when tasks fail, so check tasks directly
        if (goal.Tasks.Any(t => t.Status == WorkTaskStatus.Failed))
            return GoalLifecycleState.Failed;

        // Accepted goal — resolve post-acceptance stage from facts (later stages win)
        if (goal.Status == GoalStatus.Completed)
        {
            if (facts.IsMerged && facts.IsRecorded && facts.IsCleanedUp) return GoalLifecycleState.CleanedUp;
            if (facts.IsMerged && facts.IsRecorded) return GoalLifecycleState.Recorded;
            if (facts.IsMerged) return GoalLifecycleState.Merged;
            return GoalLifecycleState.Verified;
        }

        if (goal.Status == GoalStatus.Verified)
        {
            if (facts.IsMerged && facts.IsRecorded && facts.IsCleanedUp) return GoalLifecycleState.CleanedUp;
            if (facts.IsMerged && facts.IsRecorded) return GoalLifecycleState.Recorded;
            if (facts.IsMerged) return GoalLifecycleState.Merged;
            return GoalLifecycleState.Verified;
        }

        // All tasks done but goal not yet Completed = verifications pending
        if (goal.Tasks.All(t => t.Status == WorkTaskStatus.Completed))
            return GoalLifecycleState.AwaitingVerification;

        // Live background process exists
        if (goal.Tasks.Any(t => t.LastProcess is { IsRunning: true }))
            return GoalLifecycleState.Running;

        // Dispatch record exists (Running task status, no live process yet = waiting for execute-dispatch)
        if (goal.Tasks.Any(t => t.Status == WorkTaskStatus.Running))
            return GoalLifecycleState.Dispatched;

        // Workspace (worktree) has been created but no dispatch recorded
        if (facts.WorkspaceExists)
            return GoalLifecycleState.WorkspaceReady;

        // Active goal with no workspace yet — treat as Created
        return GoalLifecycleState.Created;
    }
}
