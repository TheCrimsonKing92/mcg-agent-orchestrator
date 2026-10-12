using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SourceSizeLandingPreflightExecutor
{
    internal static ConductorParallelAcceptanceRunResult? RunParallelLandingSourceSizePreflight(
        ConductorParallelAcceptanceCandidate candidate,
        string worktreePath)
    {
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

    internal static AcceptanceVerificationSummary? RunInlineLandingSourceSizePreflight(
        string? worktreePath,
        string? executionDirectory,
        Func<string, string?> resolveGitHead)
    {
        if (executionDirectory is null)
        {
            return null;
        }

        if (!Directory.Exists(worktreePath))
        {
            return null;
        }

        var preflight = SourceSizeRatchetPreflight.Evaluate(worktreePath!);
        return preflight.HasBlockingViolation
            ? CreateSourceSizeFailureSummary(
                preflight,
                resolveGitHead(worktreePath!),
                resolveGitHead(executionDirectory))
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
        SourceSizeRatchetPreflightResult preflight,
        Func<string, string> boundDetail) =>
    [
        SourceSizeRatchetPreflight.CheckName,
        .. SourceSizeRatchetPreflight.BlockingViolationMessages(preflight)
            .Select(boundDetail)
    ];

    internal static AcceptanceCohortReceipt? RunAcceptanceCohortSourceSizePreflight(
        string integrationPath,
        AcceptanceCohortIdentity identity,
        CohortAcceptanceStore store,
        Func<string, string> boundDetail,
        DateTimeOffset timestamp)
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
            timestamp,
            GateElapsedMilliseconds: 0,
            BuildSourceSizeReceiptFailures(preflight, boundDetail),
            GateExitCode: 1,
            GateTestResultPaths: [],
            ValidForLanding: false));
    }

    internal static MergeTrainReceipt? RunMergeTrainSourceSizePreflight(
        string integrationPath,
        MergeTrainIdentity identity,
        MergeTrainAcceptanceStore store,
        Func<string, string> boundDetail)
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
            BuildSourceSizeReceiptFailures(preflight, boundDetail),
            GateExitCode: 1,
            GateTestResultPaths: [],
            ValidForLanding: false));
    }
}
