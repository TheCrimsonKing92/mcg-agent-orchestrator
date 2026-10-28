using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LaneSubstringCollisionCheck
{
    internal const string Rule = "lane-substring-collision";

    // Both intake and completion use resolved membership, including exclusions and collection ownership.
    internal static IReadOnlyList<(AcceptanceTestLane Lane, IReadOnlyList<string> Patterns)> SubstringSelections(
        IReadOnlyList<AcceptanceTestLane> lanes, AcceptanceTestClassDescriptor descriptor) =>
        AcceptanceLaneMembership.LanesIncluding(lanes, descriptor.FullName)
            .Where(lane => !lane.Name.Equals("Remainder", StringComparison.OrdinalIgnoreCase) &&
                (descriptor.Collection is null || !lane.OwnedCollections.Contains(descriptor.Collection, StringComparer.Ordinal)))
            .Select(lane => (Lane: lane, Patterns: AcceptanceLaneMembership.MatchingIncludePatterns(lane, descriptor.FullName)))
            .Where(selection => selection.Patterns.All(pattern => pattern.StartsWith('*') && pattern.EndsWith('*')))
            .ToArray();

    internal static IReadOnlyList<TestCollectionGuardViolation> Find(
        IReadOnlyList<AcceptanceTestLane> baseLanes, IReadOnlyList<AcceptanceTestLane> candidateLanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> candidateClasses, string diffText)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(diffText);
        while (reader.ReadLine() is { } line)
        {
            var declarations = line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal) ? added
                : line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal) ? removed : null;
            if (declarations is null) continue;
            foreach (Match match in Regex.Matches(line[1..], @"\bclass\s+(\w+)"))
                declarations.Add(match.Groups[1].Value);
        }

        var violations = new List<TestCollectionGuardViolation>();
        foreach (var descriptor in candidateClasses)
        {
            var simpleName = descriptor.FullName[(Math.Max(descriptor.FullName.LastIndexOf('.'),
                descriptor.FullName.LastIndexOf('+')) + 1)..];
            if (!added.Contains(simpleName) || removed.Contains(simpleName)) continue;
            var lanes = AcceptanceLaneMembership.LanesIncluding(candidateLanes, descriptor.FullName);
            if (lanes.Count != 1 || lanes[0].Name.Equals("Remainder", StringComparison.OrdinalIgnoreCase)) continue;
            var selections = SubstringSelections(candidateLanes, descriptor);
            if (selections.Count == 0) continue;
            var (lane, patterns) = selections[0];
            var baseLane = baseLanes.FirstOrDefault(item => item.Name.Equals(lane.Name, StringComparison.OrdinalIgnoreCase));
            var basePatterns = baseLane is null ? []
                : AcceptanceLaneMembership.MatchingIncludePatterns(baseLane, descriptor.FullName);
            if (!patterns.All(pattern => pattern.StartsWith('*') && pattern.EndsWith('*') &&
                    basePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))) continue;

            var filters = string.Join(", ", patterns.Select(pattern => $"'FullyQualifiedName~{pattern.Trim('*')}'"));
            violations.Add(new TestCollectionGuardViolation(Rule, descriptor.FullName, descriptor.Collection,
                ["Remainder"], [lane.Name],
                $"Lane '{lane.Name}' selects the new class through {filters}. Rename the class so that no non-Remainder " +
                "lane substring selects it. If the class must run in this lane, its membership needs an owner-approved " +
                "filter in config/acceptance-manifest.json and an entry in that lane's pinned membership test."));
        }
        return violations;
    }
}
