namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceStructuralCoveragePartitionPlan
{
    internal static IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> Resolve(
        GoalAcceptanceVerifier.AcceptanceManifestCheck discoveryCheck,
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> effectiveChecks,
        IReadOnlyList<AcceptanceTestLane> requiredLanes)
    {
        var candidates = effectiveChecks
            .SelectMany(check => requiredLanes
                .Where(lane => IsCanonicalLaneCheck(check, discoveryCheck.Project, lane))
                .Select(lane => new Candidate(check, lane, PrefixForLane(check.Name, lane.Name))))
            .Where(candidate => candidate.Prefix is not null)
            .ToArray();
        var families = candidates
            .GroupBy(candidate => candidate.Prefix!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Family(
                group.Key,
                group.GroupBy(candidate => candidate.Lane.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(lane => lane.Key, lane => lane.ToArray(), StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(family => family.Lanes.Count)
            .ThenBy(family => family.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedFamily = families.Length == 0 ||
            (families.Length > 1 && families[0].Lanes.Count == families[1].Lanes.Count)
                ? null
                : families[0];

        return requiredLanes
            .Select(lane => ResolveLane(selectedFamily, discoveryCheck, lane))
            .ToArray();
    }

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck ResolveLane(
        Family? selectedFamily,
        GoalAcceptanceVerifier.AcceptanceManifestCheck discoveryCheck,
        AcceptanceTestLane lane)
    {
        if (selectedFamily?.Lanes.TryGetValue(lane.Name, out var matches) == true &&
            matches.Length == 1)
        {
            return matches[0].Check;
        }

        return new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = $"structural coverage missing partition: {lane.Name}",
            Type = discoveryCheck.Type,
            Project = discoveryCheck.Project,
            Arguments = ["--filter", lane.Filter],
            Runner = discoveryCheck.Runner
        };
    }

    private static bool IsCanonicalLaneCheck(
        GoalAcceptanceVerifier.AcceptanceManifestCheck check,
        string? project,
        AcceptanceTestLane lane) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        PathsMatch(check.Project, project) &&
        TryGetFilter(check.Arguments, out var filter) &&
        filter.Equals(lane.Filter, StringComparison.Ordinal) &&
        PrefixForLane(check.Name, lane.Name) is not null;

    private static string? PrefixForLane(string checkName, string laneName)
    {
        var suffix = $": {laneName}";
        return checkName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? checkName[..^suffix.Length]
            : null;
    }

    private static bool PathsMatch(string? left, string? right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : path.Replace('\\', '/').Trim().TrimStart('.').TrimStart('/');

    private static bool TryGetFilter(IReadOnlyList<string> arguments, out string filter)
    {
        filter = string.Empty;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                filter = arguments[index + 1];
                return !string.IsNullOrWhiteSpace(filter);
            }
        }

        return false;
    }

    private sealed record Candidate(
        GoalAcceptanceVerifier.AcceptanceManifestCheck Check,
        AcceptanceTestLane Lane,
        string? Prefix);

    private sealed record Family(
        string Prefix,
        IReadOnlyDictionary<string, Candidate[]> Lanes);
}
