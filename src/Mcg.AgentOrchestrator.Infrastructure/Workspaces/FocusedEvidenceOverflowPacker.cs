using FocusedEvidenceFilter = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilter;
using FocusedEvidencePlannedCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidencePlannedCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceOverflowPacker
{
    internal static IReadOnlyList<FocusedEvidencePlannedCheck> Pack(
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> compatible,
        string project)
    {
        var checks = new List<FocusedEvidencePlannedCheck>();
        var group = new List<(string Target, string Project, FocusedEvidenceFilter? Filter)>();
        foreach (var item in compatible
            .OrderBy(item => item.Filter!.CanonicalText, StringComparer.Ordinal)
            .ThenBy(item => item.Target, StringComparer.Ordinal))
        {
            var candidateTokens = group.Append(item)
                .SelectMany(member => member.Filter!.Tokens)
                .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
                .OrderBy(token => token.CanonicalToken, StringComparer.Ordinal)
                .ToArray();
            var candidateCanonical = string.Join("|", candidateTokens.Select(token => token.CanonicalToken));
            if (group.Count > 0 &&
                (candidateCanonical.Length > GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength ||
                 item.Filter!.CanonicalText.Length >= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength))
            {
                AddGroup();
            }

            group.Add(item);
            // Boundary-sized items stay whole and occupy their own check.
            if (item.Filter!.CanonicalText.Length >= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)
            {
                AddGroup();
            }
        }

        if (group.Count > 0)
        {
            AddGroup();
        }

        return checks;

        void AddGroup()
        {
            if (group.Count == 1)
            {
                var item = group[0];
                checks.Add(new FocusedEvidencePlannedCheck(
                    [item.Target], item.Project, item.Filter, [item.Filter!]));
            }
            else
            {
                var groupTokens = group
                    .SelectMany(item => item.Filter!.Tokens)
                    .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
                    .OrderBy(token => token.CanonicalToken, StringComparer.Ordinal)
                    .ToArray();
                checks.Add(new FocusedEvidencePlannedCheck(
                    group.Select(item => item.Target).Distinct(StringComparer.Ordinal).ToArray(),
                    project,
                    new FocusedEvidenceFilter(
                        string.Join("; ", group.Select(item => item.Target)),
                        string.Join("|", groupTokens.Select(token => token.CanonicalToken)),
                        groupTokens),
                    group.Select(item => item.Filter!).ToArray()));
            }

            group.Clear();
        }
    }
}
