namespace Mcg.AgentOrchestrator.Core;

internal static class PlannerEvidenceBriefSection
{
    internal static IReadOnlyList<string> Build(Goal goal, IEnumerable<HumanInputRequest> requests)
    {
        var plannerEvidenceLines = requests.Where(item => item.GoalId == goal.Id && item.PlannerEvidence is not null)
            .Select(item => $"- identity={System.Text.Json.JsonSerializer.Serialize(item.PlannerIdentity)}; " +
                $"owner={item.EvidenceOwner ?? "operator"}; availability={item.PlannerEvidence!.Availability}; " +
                $"state={(item.IsCompleted ? "answered" : "pending")}; " +
                $"brief={(item.PlannerIdentity?.BriefVersion == goal.AuthoritativeBrief.Version ? "current" : "historical/unversioned")}; " +
                $"next={(item.IsCompleted ? "consult bound answer only" : item.ResumeCommand)}; " +
                $"payload={System.Text.Json.JsonSerializer.Serialize(item.PlannerEvidence)}")
            .Concat(goal.GateTestObligations.Select(item =>
                $"- identity={System.Text.Json.JsonSerializer.Serialize(item.PlannerIdentity)}; owner=acceptance-gate; " +
                $"availability=post-implementation; brief_version={item.BriefVersion}; scope={(item.BriefVersion == goal.AuthoritativeBrief.Version ? "current" : "historical")}; " +
                $"next={(item.BriefVersion == goal.AuthoritativeBrief.Version ? "Acceptance executes exact-candidate selectors" : "consult historical requirement only")}; " +
                $"payload={System.Text.Json.JsonSerializer.Serialize(item.PlannerEvidence)}; selectors={string.Join(", ", item.Selectors)}"))
            .ToList();
        if (plannerEvidenceLines.Count > 0)
        {
            plannerEvidenceLines.Insert(0, "## Independent Planner Evidence Requirements");
            plannerEvidenceLines.Add("Historical answers authorize only their bound requirement; future requirements do not block active work.");
        }
        return plannerEvidenceLines;
    }
}
