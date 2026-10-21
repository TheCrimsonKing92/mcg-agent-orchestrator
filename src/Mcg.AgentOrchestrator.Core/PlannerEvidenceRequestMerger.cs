namespace Mcg.AgentOrchestrator.Core;

internal static class PlannerEvidenceRequestMerger
{
    // Validation and exact deduplication, never a union of unrelated requirements.
    internal static HumanInputDirectiveParseResult? TryMerge(IReadOnlyList<HumanInputDirective> requests)
    {
        var distinct = new List<HumanInputDirective>();
        foreach (var request in requests)
        {
            var payload = request.PlannerEvidence!;
            var existing = distinct.FirstOrDefault(item => item.PlannerEvidence!.CriterionIndex == payload.CriterionIndex &&
                item.PlannerEvidence.EvidenceKey == payload.EvidenceKey);
            if (existing is not null)
            {
                if (!payload.SemanticallyEquals(existing.PlannerEvidence))
                    return new(null, $"Conflicting PLANNER_EVIDENCE_REQUEST identity criterion={payload.CriterionIndex} key={payload.EvidenceKey}; re-emit the original payload or request a new brief.");
                continue;
            }
            distinct.Add(request);
        }
        return new HumanInputDirectiveParseResult(distinct.Count == 1 ? distinct[0] : null, null)
            { Directives = distinct.ToArray() };
    }
}