namespace Mcg.AgentOrchestrator.Core;

public static class PostLandingOperatorCheck
{
    public const string Availability = "post-landing";

    public static bool IsPostLandingCheck(HumanInputRequest request) =>
        request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
        string.Equals(request.PlannerEvidence?.Availability, Availability, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<HumanInputRequest> HoldingLanding(IEnumerable<HumanInputRequest> pending) =>
        pending.Where(request => !IsPostLandingCheck(request)).ToArray();

    public static string? DescribeOpen(IEnumerable<HumanInputRequest> pending)
    {
        var checks = pending.Where(IsPostLandingCheck).Select(request =>
            $"{request.Id.Value[..Math.Min(8, request.Id.Value.Length)]} " +
            $"(criterion {request.PlannerEvidence!.CriterionIndex}: {request.PlannerEvidence.Needed})").ToArray();
        return checks.Length == 0 ? null : "open post-landing operator check(s): " + string.Join("; ", checks);
    }
}
