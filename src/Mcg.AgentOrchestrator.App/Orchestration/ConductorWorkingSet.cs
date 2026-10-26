using Mcg.AgentOrchestrator.Core;
namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorWorkingSet
{
    internal static bool IsTerminalGoal(Goal goal) =>
        goal.Status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    internal static int CountRecheckableNonTerminalGoals(
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

    internal static bool IsPreWalkExcludedGoal(Goal goal) =>
        goal.Status == GoalStatus.Parked || IsPreWalkExcludedTerminalGoal(goal);

    internal static bool IsPreWalkExcludedTerminalGoal(Goal goal) =>
        GoalStatusSemantics.ExcludesFromConductorWorkingSet(goal.Status)
        || IsStaleTerminalGoalWithAssignedWork(goal);

    internal static bool IsLoopEligibleGoal(Goal goal, ConductorDriver driver, GoalProjectionCache goalProjectionCache)
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

    internal static bool IsStaleTerminalGoalWithAssignedWork(Goal goal) =>
        (goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed) &&
        goal.Tasks.Any(task => task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman);
}
