using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, bool, CancellationToken,
        FindingEvidenceNegativeControl, IReadOnlyList<string>?, FindingEvidenceMutation?, FocusedEvidenceRunResult> _runNegativeControlFocusedEvidence;

    private Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult>
        SelectFindingEvidenceRunner(FindingEvidenceNegativeControl? mode, bool runBaselineArm,
            IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null) =>
        mode is { } control
            ? (goal, request, lease, token) =>
                _runNegativeControlFocusedEvidence(goal, request, lease, runBaselineArm, token, control, revertPaths, mutation)
            : runBaselineArm ? _runDualArmFocusedEvidence : _runFocusedEvidence;

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
