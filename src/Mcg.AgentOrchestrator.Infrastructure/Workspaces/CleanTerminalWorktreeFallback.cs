using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CleanTerminalWorktreeFallback
{
    public static bool Allows(string path, AgentOrchestratorKernel? kernel, GoalId goalId)
    {
        var goal = kernel?.Goals.FirstOrDefault(item => item.Id == goalId);
        if (goal is null || !GoalWorktrees.IsTerminalCleanupStatus(goal.Status))
            return false;

        var status = GitCli.Run(path, "status", "--porcelain");
        return status.ExitCode == 0 && string.IsNullOrWhiteSpace(status.Output);
    }
}
