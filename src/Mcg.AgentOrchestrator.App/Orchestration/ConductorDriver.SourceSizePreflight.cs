using Mcg.AgentOrchestrator.Core;
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

        var summary = CreateSourceSizeFailureSummary(
            preflight,
            candidate.BranchHeadSha,
            candidate.MainHeadSha);
        return ConductorParallelAcceptanceRunResult.Accepted(candidate, summary);
    }

    private AcceptanceVerificationSummary? RunInlineLandingSourceSizePreflight(Goal goal)
    {
        if (_executionDirectory is null)
        {
            return null;
        }

        var worktreePath = GoalWorktrees.WorktreePath(_executionDirectory, goal.Id);
        if (!Directory.Exists(worktreePath))
        {
            return null;
        }

        var preflight = SourceSizeRatchetPreflight.Evaluate(worktreePath);
        return preflight.HasBlockingViolation
            ? CreateSourceSizeFailureSummary(
                preflight,
                TryResolveGitHead(worktreePath),
                TryResolveGitHead(_executionDirectory))
            : null;
    }

    private static AcceptanceVerificationSummary CreateSourceSizeFailureSummary(
        SourceSizeRatchetPreflightResult preflight,
        string? branchHeadSha,
        string? mainHeadSha)
    {
        var check = new AcceptanceCheckResult(
            SourceSizeRatchetPreflight.CheckName,
            false,
            1,
            preflight.Message);
        return new AcceptanceVerificationSummary(
            false,
            [check],
            preflight.Message,
            [check.Name],
            branchHeadSha,
            mainHeadSha);
    }

    private static IReadOnlyList<string> BuildSourceSizeReceiptFailures(
        SourceSizeRatchetPreflightResult preflight) =>
    [
        SourceSizeRatchetPreflight.CheckName,
        .. SourceSizeRatchetPreflight.BlockingViolationMessages(preflight)
            .Select(BoundCohortDetail)
    ];

    private AcceptanceCohortReceipt? RunAcceptanceCohortSourceSizePreflight(
        string integrationPath,
        AcceptanceCohortIdentity identity,
        CohortAcceptanceStore store)
    {
        var preflight = SourceSizeRatchetPreflight.Evaluate(integrationPath);
        if (!preflight.HasBlockingViolation)
        {
            return null;
        }

        return store.SaveGateReceipt(new AcceptanceCohortReceipt(
            $"cohort-receipt-v2-{identity.Value[(AcceptanceCohortIdentity.Version.Length + 1)..]}",
            identity,
            AcceptanceCohortGateOutcome.Failed,
            _utcNow(),
            GateElapsedMilliseconds: 0,
            BuildSourceSizeReceiptFailures(preflight),
            GateExitCode: 1,
            GateTestResultPaths: [],
            ValidForLanding: false));
    }

    private static MergeTrainReceipt? RunMergeTrainSourceSizePreflight(
        string integrationPath,
        MergeTrainIdentity identity,
        MergeTrainAcceptanceStore store)
    {
        var preflight = SourceSizeRatchetPreflight.Evaluate(integrationPath);
        if (!preflight.HasBlockingViolation)
        {
            return null;
        }

        return store.SaveGateReceipt(new MergeTrainReceipt(
            $"merge-train-receipt-{identity.Value[(MergeTrainIdentity.Version.Length + 1)..]}",
            identity,
            MergeTrainGateOutcome.Failed,
            DateTimeOffset.UtcNow,
            GateElapsedMilliseconds: 0,
            BuildSourceSizeReceiptFailures(preflight),
            GateExitCode: 1,
            GateTestResultPaths: [],
            ValidForLanding: false));
    }

    private static bool IsSourceSizeContentFailure(AcceptanceVerificationResult result) =>
        result.Checks?.Any(check =>
            !check.Passed &&
            !check.Advisory &&
            string.Equals(check.Name, SourceSizeRatchetPreflight.CheckName, StringComparison.Ordinal)) == true;
}
