using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, bool, CancellationToken,
        FindingEvidenceNegativeControl, FocusedEvidenceRunResult> _runNegativeControlFocusedEvidence;

    private Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult>
        SelectFindingEvidenceRunner(FindingEvidenceNegativeControl? mode, bool runBaselineArm) =>
        mode is { } control
            ? (goal, request, lease, token) =>
                _runNegativeControlFocusedEvidence(goal, request, lease, runBaselineArm, token, control)
            : runBaselineArm ? _runDualArmFocusedEvidence : _runFocusedEvidence;

    private static FocusedEvidenceRunResult RetainNegativeControlEvidence(
        FocusedEvidenceRunResult next, FocusedEvidenceRunResult prior)
    {
        if (prior.NegativeControlOutcome is null) return next;
        return next with
        {
            NegativeControlOutcome = prior.NegativeControlOutcome,
            Arms = [.. next.Arms ?? [], .. (prior.Arms ?? []).Where(arm => arm.Arm == FindingEvidenceArm.SourceReverted)],
            Summary = $"{next.Summary}; {FindingEvidenceNegativeControlOutcomeJsonConverter.ToWireValue(prior.NegativeControlOutcome.Value)}"
        };
    }
}
