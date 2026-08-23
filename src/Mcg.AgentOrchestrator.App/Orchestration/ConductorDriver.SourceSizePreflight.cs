using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorParallelAcceptanceRunResult? RunParallelLandingSourceSizePreflight(
        ConductorParallelAcceptanceCandidate candidate)
    {
        var worktreePath = GoalWorktrees.WorktreePath(_executionDirectory!, candidate.Goal.Id);
        var preflight = SourceSizeRatchetPreflight.Evaluate(worktreePath);
        if (!preflight.HasBlockingViolation)
        {
            return null;
        }

        var check = new AcceptanceCheckResult(
            "source size ratchet preflight", false, 1, preflight.Message);
        var summary = new AcceptanceVerificationSummary(
            false,
            [check],
            preflight.Message,
            [check.Name],
            candidate.BranchHeadSha,
            candidate.MainHeadSha);
        return ConductorParallelAcceptanceRunResult.Accepted(candidate, summary);
    }
}
