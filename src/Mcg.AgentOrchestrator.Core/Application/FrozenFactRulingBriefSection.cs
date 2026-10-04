namespace Mcg.AgentOrchestrator.Core;

internal static class FrozenFactRulingBriefSection
{
    internal const int CharacterCap = 6_000;
    internal const string DeveloperInstruction =
        "Make only each ruling's allowed change and run its diff check on your own diff before reporting.";
    internal const string ReviewerInstruction =
        "For each ruling, read the candidate diff of each named test file: every changed line must sit inside an amended fact and match its allowed change, and every other fact in a frozen class must be unchanged; report any other edit as a blocking finding with stable id `frozen-fact-scope-<Class.Method>`.";

    internal static IReadOnlyList<string> Render(
        IEnumerable<HumanInputRequest> requests, GoalId goalId, AgentRole role)
    {
        var rulings = requests.Where(request => request.GoalId == goalId && request.IsCompleted &&
                !request.WasDismissed && request.SupersededByRequestId is null &&
                !request.IsSyntheticParkedHumanWaitCompletion &&
                HumanWaitPolicyDefaults.IsSpecClarificationClass(request.Kind))
            .OrderBy(request => request.AnsweredAt).ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .Select(request => (Request: request, Ruling: FrozenFactRuling.TryParse(request.AuthoritativeAnswer?.Text)))
            .Where(entry => entry.Ruling is not null).ToArray();
        if (rulings.Length == 0) return [];
        var lines = new List<string> { "## Frozen-fact rulings" };
        var omitted = new List<string>();
        var used = 0;
        foreach (var (request, ruling) in rulings)
        {
            var rendered = ruling!.Render();
            if (omitted.Count > 0 || used + rendered.Length > CharacterCap)
            {
                omitted.Add(request.Id.Value);
                continue;
            }
            used += rendered.Length;
            lines.Add($"Request {request.Id.Value}:");
            lines.AddRange(rendered.ReplaceLineEndings("\n").Split('\n'));
        }
        if (omitted.Count > 0)
            lines.Add($"Omitted over the 6,000-character cap: {string.Join(", ", omitted)}.");
        if (role == AgentRole.Developer) lines.Add(DeveloperInstruction);
        if (role == AgentRole.Reviewer) lines.Add(ReviewerInstruction);
        lines.Add(string.Empty);
        return lines;
    }
}
