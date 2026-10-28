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
        if (!runBaselineArm && mode == FindingEvidenceNegativeControl.RevertSrc &&
            revertPaths is { Count: > 0 } && mutation is null)
            return new SourceRevertFocusedRunner((goal, request, lease, token, continuation) =>
                _runNegativeControlFocusedEvidence(goal, request, lease, false, token,
                    FindingEvidenceNegativeControl.RevertSrc, revertPaths, null, continuation)).Run;
        return mode is { } control
            ? (goal, request, lease, token) =>
                _runNegativeControlFocusedEvidence(goal, request, lease, runBaselineArm, token, control, revertPaths, mutation, null)
            : runBaselineArm ? _runDualArmFocusedEvidence : _runFocusedEvidence;
    }

    // Preserve the existing delegate API while carrying a typed invocation capability to its owner.
    internal sealed class SourceRevertFocusedRunner(
        Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, SourceRevertedEvidenceContinuation?, FocusedEvidenceRunResult> runner)
    {
        internal FocusedEvidenceRunResult Run(Goal goal, string request, DotnetBuildEnvironmentLease? lease, CancellationToken token) =>
            runner(goal, request, lease, token, null);
        internal FocusedEvidenceRunResult RunWithContinuation(Goal goal, string request, DotnetBuildEnvironmentLease? lease,
            CancellationToken token, SourceRevertedEvidenceContinuation? continuation) => runner(goal, request, lease, token, continuation);
    }

    internal ConductorParallelAcceptanceRunResult RunContinuableSourceRevertEvidence(
        ConductorParallelAcceptanceCandidate candidate, string request, DotnetBuildEnvironmentLease? lease,
        CancellationToken token, IReadOnlyList<string> paths, SourceRevertedEvidenceContinuation? continuation) =>
        ConductorParallelAcceptanceRunResult.Focused(candidate,
            _runNegativeControlFocusedEvidence(candidate.Goal, request, lease, false, token,
                FindingEvidenceNegativeControl.RevertSrc, paths, null, continuation));

    private static FocusedEvidenceRunResult RetainNegativeControlEvidence(
        FocusedEvidenceRunResult next, FocusedEvidenceRunResult prior)
    {
        if (prior.NegativeControlOutcome is null) return next;
        return next with
        {
            NegativeControlOutcome = prior.NegativeControlOutcome,
            RevertPathsRejection = prior.RevertPathsRejection,
            Arms = [.. next.Arms ?? [], .. (prior.Arms ?? []).Where(arm => arm.Arm == FindingEvidenceArm.SourceReverted)],
            Summary = $"{next.Summary}; " + (prior.RevertPathsRejection is not null ||
                prior.NegativeControlOutcome == FindingEvidenceNegativeControlOutcome.CompileRed ? prior.Summary :
                FindingEvidenceNegativeControlOutcomeJsonConverter.ToWireValue(prior.NegativeControlOutcome.Value))
        };
    }
}
