using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedAdmissionLandingHold
{
    internal static bool IsGateSatisfiable(HumanInputRequest wait) =>
        wait.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
        GateTestObligation.IsAcceptanceGateOwner(wait.EvidenceOwner) &&
        wait.TestNames is { Count: > 0 };

    internal static HumanInputRequest? FindBlockingWait(IReadOnlyList<HumanInputRequest> pending) =>
        pending.FirstOrDefault(wait => !wait.IsCompleted && !IsGateSatisfiable(wait));

    internal static GateReadyCandidateProjectionResult Apply(
        GateReadyCandidateProjectionResult projection, IReadOnlyList<HumanInputRequest> pending) =>
        projection is GateReadyCandidateProjectionResult.Ready && FindBlockingWait(pending) is { } wait
            ? new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.LandingHeld)
                { HoldRequestId = wait.Id.Value }
            : projection;
}
