namespace Mcg.AgentOrchestrator.Core;

// Identifies report waits and recorded classes; candidate-bound validation stays at landing.
public static class DeveloperReportWait
{
    public static bool IsReportWait(Goal goal, HumanInputRequest request)
    {
        if (request.GoalId != goal.Id || request.IsCompleted ||
            request.Kind != HumanWaitKind.ProspectiveAcceptanceEvidence ||
            !string.Equals(request.PlannerEvidence?.Availability, "post-implementation", StringComparison.OrdinalIgnoreCase))
            return false;

        var owner = request.EvidenceOwner?.Trim();
        if (!string.Equals(owner, "developer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(owner, "worker", StringComparison.OrdinalIgnoreCase))
            return false;

        return ReportCriterionLookup.Find(goal, request.PlannerIdentity) is { } criterion &&
            DeveloperReportEvidence.FindRequiredField(criterion) is not null;
    }

    public static bool HasReportedClasses(Goal goal)
    {
        var developer = goal.Tasks
            .Where(task => task.RequiredRole == AgentRole.Developer && task.LastDispatch is not null)
            .MaxBy(task => task.LastDispatch!.DispatchedAt);
        return developer?.Status == WorkTaskStatus.Completed &&
            DeveloperReportEvidence.TryReadReport(developer, out var report) &&
            DeveloperDeferredTestClassNames.Parse(report).Count > 0;
    }
}
