using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainReproductionTree
{
    internal static string Read(string workspace)
    {
        var status = GitCli.Run(workspace, "status", "--porcelain", "--untracked-files=no");
        return status.ExitCode == 0 && string.IsNullOrWhiteSpace(status.Output)
            ? GoalWorktrees.ResolveRequiredRef(workspace, "HEAD^{tree}") : "dirty-or-unreadable-tree";
    }
}
