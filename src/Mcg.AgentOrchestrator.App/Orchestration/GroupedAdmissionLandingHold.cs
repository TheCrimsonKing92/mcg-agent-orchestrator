using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedAdmissionLandingHold
{
    internal static bool IsGateSatisfiable(HumanInputRequest wait) =>
        wait.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
        GateTestObligation.IsAcceptanceGateOwner(wait.EvidenceOwner) &&
        wait.TestNames is { Count: > 0 };

    internal static HumanInputRequest? FindBlockingWait(IReadOnlyList<HumanInputRequest> pending, Goal? goal = null) =>
        pending.FirstOrDefault(wait => !wait.IsCompleted && !PostLandingOperatorCheck.IsPostLandingCheck(wait) && !IsGateSatisfiable(wait) &&
            !(goal is not null && DeveloperReportWait.IsReportWait(goal, wait) && DeveloperReportWait.HasReportedClasses(goal)));

    internal static GateReadyCandidateProjectionResult Apply(
        GateReadyCandidateProjectionResult projection, IReadOnlyList<HumanInputRequest> pending, Goal? goal = null) =>
        projection is GateReadyCandidateProjectionResult.Ready && FindBlockingWait(pending, goal) is { } wait
            ? new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.LandingHeld)
                { HoldRequestId = wait.Id.Value }
            : projection;
}
