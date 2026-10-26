namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingEvidenceFailureSelectionMatch
{
    internal static bool Matches(string identity, string selection) =>
        (FindingEvidenceRoundPlanner.TryExpandClasses(selection, out var names)
            ? names : [selection.Trim()]).Any(name => MatchesName(identity, name));

    internal static bool MatchesName(string identity, string selection)
    {
        var start = 0;
        while ((start = identity.IndexOf(selection, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + selection.Length;
            var leftBoundary = start == 0 || identity[start - 1] is '.' or '+';
            var rightBoundary = end == identity.Length || identity[end] is '.' or '+' or '(' or '[';
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start++;
        }

        return false;
    }
}
