using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Owns CLI workspace publication and applying its result to the acceptance transaction.
internal static class CliWorkspaceMerge
{
    // Capture publication at the successful CLI merge boundary. A later HEAD or
    // recovery clock cannot establish when the original duty became due.
    internal static GoalWorktreeMergeResult? MergeWithPublication(
        CliExecutionContext context, Goal goal, out GoalPromotionFact? publication,
        Func<string?> mutationBlocker)
    {
        publication = null;
        var duties = goal.OutstandingPostLandingDuties;
        var needsPublication = duties.Any(item => item.Publication is null);
        var root = context.Workspace.ExecutionDirectory;
        // Publication authority requires the integration target. Goals without later
        // duties retain the existing worktree-service merge contract.
        if (duties.Count > 0)
        {
            var target = GitCli.Run(root, "symbolic-ref", "--quiet", "HEAD");
            if (!target.Succeeded || !string.Equals(target.Output.Trim(),
                    $"refs/heads/{context.Workspace.IntegrationBranch}", StringComparison.Ordinal))
            {
                return new GoalWorktreeMergeResult(false, context.Worktrees.BranchName(goal.Id),
                    $"Fast-forward blocked: HEAD must be attached to integration branch '{context.Workspace.IntegrationBranch}'.",
                    null);
            }
        }
        var priorMain = needsPublication ? ReadRevision("HEAD") : null;
        var candidate = needsPublication ? ReadRevision(context.Worktrees.BranchName(goal.Id)) : null;
        var merge = context.Worktrees.TryFastForwardMerge(root, goal.Id, mutationBlocker);
        if (merge?.FastForwarded != true || !needsPublication) return merge;

        var at = (context.DiagnosticsClock ?? new SystemClock()).UtcNow;
        var promotedMain = ReadRevision("HEAD");
        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        if (!string.Equals(priorMain, promotedMain, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(candidate, promotedMain, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CLI publication does not match the captured goal candidate.");
            repository.RecordPromotionFacts([new GoalPromotionFact(goal.Id.Value, candidate!, priorMain!,
                promotedMain, at, GoalPromotionFact.CliFastForwardProvenance)]);
        }
        // An already-merged branch may recover a known fact, but never invent one.
        publication = repository.ReadPromotionFactAsync(goal.Id).GetAwaiter().GetResult();
        if (publication is not null) context.Kernel.BindPostLandingPublication(goal.Id, publication);
        return merge;

        string ReadRevision(string revision)
        {
            var result = GitCli.Run(root, "rev-parse", revision);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Output))
                throw new InvalidOperationException($"Cannot capture CLI publication revision '{revision}': {result.Error}");
            return result.Output.Trim();
        }
    }

    internal static void ApplyAcceptanceResult(
        AgentOrchestratorKernel kernel,
        AcceptanceMergeCommitRequest request,
        AcceptanceMergeCommitResult result)
    {
        if (result.FastForwarded)
        {
            if (result.Publication is { } publication)
                kernel.BindPostLandingPublication(request.GoalId, publication);
            kernel.ClearAcceptanceFailure(request.GoalId);
            kernel.CompleteGoal(request.GoalId, request.CompletionReason);
        }
        else if (result.Message is not null)
        {
            kernel.RecordAcceptanceFailure(request.GoalId, ["merge"]);
        }
    }
}
