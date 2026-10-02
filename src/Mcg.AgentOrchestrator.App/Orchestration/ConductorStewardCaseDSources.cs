using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorStewardCaseDSources(
    Func<Goal, string?> BranchHead,
    Func<Goal, string, string?> GenuineReason,
    Func<Goal, string, IReadOnlyList<string>> ChangedPaths,
    Func<Goal, IReadOnlyList<string>> TrxPaths,
    AcceptanceFailingTestIndex Index,
    int RegateCap = ApparatusRedGate.DefaultPerGoalRegateCap)
{
    internal string? ResolveHead(Goal goal)
    {
        try { return BranchHead(goal); }
        catch { return null; }
    }

    internal static AcceptanceFailingTestIndex CreateIndex(OrchestratorWorkspace workspace) =>
        new(Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    internal static ConductorStewardCaseDSources CreateDefault(OrchestratorWorkspace workspace) => new(
        goal =>
        {
            var worktree = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
            if (worktree is null) return null;
            var result = GitCli.Run(worktree, "rev-parse", "HEAD");
            return result.Succeeded ? result.Output.Trim() : null;
        },
        (goal, sha) => ReadGenuineReason(workspace.ExecutionDirectory, goal, sha),
        (goal, sha) =>
        {
            var worktree = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
            if (worktree is null) return ["changed paths unavailable: goal worktree missing"];
            var result = GitCli.Run(worktree, "diff", "--name-only", $"main...{sha}");
            return result.Succeeded
                ? result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                : ["changed paths unavailable: git diff failed"];
        },
        goal => ConductorStewardAcceptanceTrxResolver.Resolve(workspace.OrchestratorDirectory, goal),
        CreateIndex(workspace));

    private static string? ReadGenuineReason(string executionDirectory, Goal goal, string sha)
    {
        try
        {
            return GoalOperationJournal.Read(executionDirectory, goal.Id).Entries
                .Where(entry => entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation &&
                    string.Equals(entry.BranchHeadSha, sha, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.At).FirstOrDefault()?.Detail;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"genuine reason unavailable ({ex.GetType().Name})";
        }
    }
}
