using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, bool, CancellationToken,
        FindingEvidenceNegativeControl, IReadOnlyList<string>?, FindingEvidenceMutation?, SourceRevertedEvidenceContinuation?, FocusedEvidenceRunResult> _runNegativeControlFocusedEvidence;

    private Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult>
        SelectFindingEvidenceRunner(FindingEvidenceNegativeControl? mode, bool runBaselineArm,
            IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null)
    {
        return ConductorParallelAcceptanceAttemptCoordinator.SourceRevertFocusedRunner.TryCreate(
            mode, runBaselineArm, revertPaths, mutation, _runNegativeControlFocusedEvidence) ?? (mode is { } control
            ? (goal, request, lease, token) =>
                _runNegativeControlFocusedEvidence(goal, request, lease, runBaselineArm, token, control, revertPaths, mutation, null)
            : runBaselineArm ? _runDualArmFocusedEvidence : _runFocusedEvidence);
    }

    internal ConductorParallelAcceptanceRunResult RunContinuableSourceRevertEvidence(
        ConductorParallelAcceptanceCandidate candidate, string request, DotnetBuildEnvironmentLease? lease,
        CancellationToken token, IReadOnlyList<string> paths, SourceRevertedEvidenceContinuation? continuation) =>
        ConductorParallelAcceptanceRunResult.Focused(candidate,
            _runNegativeControlFocusedEvidence(candidate.Goal, request, lease, false, token,
                FindingEvidenceNegativeControl.RevertSrc, paths, null, continuation));

    private static FocusedEvidenceRunResult RetainNegativeControlEvidence(
        FocusedEvidenceRunResult next, FocusedEvidenceRunResult prior) =>
        ConductorParallelAcceptanceAttemptCoordinator.RetainNegativeControlEvidence(next, prior);
}
