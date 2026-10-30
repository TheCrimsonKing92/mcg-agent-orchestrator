using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainFocusedRecheckRunner
{
    internal delegate RunResult? Runner(string workspacePath, MergeTrainIdentity identity,
        ConductorCohortFocusedSelection selection, DotnetBuildEnvironmentLease lease, CancellationToken cancellationToken);

    internal sealed record RunResult(string AttemptId, string MainRevision, ConductorCohortFocusedPassResult Verification,
        int NotRunSelectionCount = 0);

    internal static MergeTrainReproductionAttribution.RunResult? ReadExecution(IReadOnlyList<string> paths)
    {
        try { return MergeTrainReproductionAttribution.ReadResults(paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or
            ArgumentException or InvalidOperationException) { return null; }
    }

    internal static Runner CreateLiveRunner(IGoalAcceptanceVerifier verifier, OrchestratorWorkspace workspace) =>
        (path, identity, selection, lease, token) =>
        {
            // The attempt-1 train workspace is the partition containing all remaining
            // members. A single-member attribution partition has a different tree.
            var attemptId = $"merge-train-recheck-{Guid.NewGuid():N}";
            var main = GoalWorktrees.ResolveRequiredRef(path, $"refs/heads/{workspace.IntegrationBranch}");
            var tree = GoalWorktrees.ResolveRequiredRef(path, "HEAD^{tree}");
            if (main != identity.ObservedMainRevision || tree != identity.TrainTreeRevision) return null;
            var clock = Stopwatch.StartNew();
            var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, path, identity.Members[0].GoalId,
                selection.Request, lease.Environment.BuildPermitIndex, lease, runBaselineArm: false, token,
                integrationBranch: workspace.IntegrationBranch, projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull);
            var interpreted = ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection) with
            { ElapsedMilliseconds = clock.ElapsedMilliseconds, TreeRevision = tree };
            if (result.OutcomeReason is FindingEvidenceOutcomeReason.CandidateInconclusive or FindingEvidenceOutcomeReason.ApparatusFailure)
                interpreted = interpreted with { Kind = ConductorCohortFocusedPassKind.InfrastructureFailure };
            if (GoalWorktrees.ResolveRequiredRef(path, $"refs/heads/{workspace.IntegrationBranch}") != main ||
                GoalWorktrees.ResolveRequiredRef(path, "HEAD^{tree}") != tree) return null;
            return new(attemptId, main, interpreted, result.Arms?.Sum(arm => arm.NotRunSelections?.Count ?? 0) ?? 0);
        };
}
