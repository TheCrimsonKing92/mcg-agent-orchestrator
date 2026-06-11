using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalWorktreeDiffProvider
{
    public static Func<GoalId, string?> Create(string executionDirectory) =>
        goalId => GoalWorktrees.TryGetBranchDiff(executionDirectory, goalId);
}
