namespace Mcg.AgentOrchestrator.Core;

internal static class PlannerEvidenceRequestMerger
{
    internal static HumanInputDirectiveParseResult? TryMerge(IReadOnlyList<HumanInputDirective> requests)
    {
        // A gate may resolve the whole wait, so every obligation must belong to that gate.
        if (requests.Count == 0 || requests.Any(request =>
            request.Kind != HumanWaitKind.ProspectiveAcceptanceEvidence ||
            !string.Equals(request.EvidenceOwner, "acceptance-gate", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var first = requests[0];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var testNames = new List<string>();
        foreach (var request in requests)
        {
            foreach (var testName in request.TestNames ?? [])
            {
                if (seen.Add(testName))
                {
                    testNames.Add(testName);
                }
            }
        }

        return new HumanInputDirectiveParseResult(first with
        {
            Question = string.Join(" ", requests.Select(request => request.Question)),
            TestNames = testNames.Count == 0 ? null : testNames.ToArray()
        }, null);
    }
}
