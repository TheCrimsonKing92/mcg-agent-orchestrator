namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaneMembership
{
    internal static IReadOnlyList<AcceptanceTestLane> LanesIncluding(
        IReadOnlyList<AcceptanceTestLane> lanes, string classFullName) =>
        lanes.Where(lane => Includes(lane, classFullName)).ToArray();

    internal static IReadOnlyList<AcceptanceTestLane> ResolveOwnedCollections(
        IReadOnlyList<AcceptanceTestLane> lanes, string worktreePath) =>
        lanes.All(lane => lane.OwnedCollections.Count == 0)
            ? lanes
            : ResolveOwnedCollections(lanes, AcceptanceTestClassSourceScanner.Scan(worktreePath));

    internal static IReadOnlyList<AcceptanceTestLane> ResolveOwnedCollections(
        IReadOnlyList<AcceptanceTestLane> lanes, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        if (lanes.All(lane => lane.OwnedCollections.Count == 0)) return lanes;
        var result = lanes.ToArray();
        foreach (var entry in classes.OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            if (entry.Collection is null) continue;
            var ownerIndex = Array.FindIndex(result, lane => lane.OwnedCollections.Contains(entry.Collection, StringComparer.Ordinal));
            if (ownerIndex < 0) continue;
            var className = entry.FullName;
            if (ExplicitlyExcludes(result[ownerIndex], className))
            {
                if (!LanesIncluding(result, className).Any(lane =>
                        !lane.Name.Equals("Remainder", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Owned test class '{className}' has no named acceptance lane after its owner's explicit exclusion.");
            }
            else
            {
                if (!Includes(result[ownerIndex], className))
                {
                    result[ownerIndex] = result[ownerIndex] with
                    {
                        Filter = $"{result[ownerIndex].Filter}|FullyQualifiedName={className}"
                    };
                }
                if (!Includes(result[ownerIndex], className))
                    throw new InvalidDataException($"Owned test class '{className}' cannot be selected by its acceptance lane.");
            }
            for (var index = 0; index < result.Length; index++)
            {
                if (index == ownerIndex || !result[index].Name.Equals("Remainder", StringComparison.OrdinalIgnoreCase) ||
                    !Includes(result[index], className)) continue;
                result[index] = result[index] with
                {
                    Filter = $"{result[index].Filter}&FullyQualifiedName!={className}"
                };
            }
        }
        return result;
    }

    private static bool Includes(AcceptanceTestLane lane, string classFullName)
    {
        var args = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
        var included = new List<string>();
        var excluded = new List<string>();
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length)
                throw new InvalidDataException($"Lane '{lane.Name}' translated to an incomplete MTP filter.");
            switch (args[index])
            {
                case "--filter-class": included.Add(args[index + 1]); break;
                case "--filter-not-class": excluded.Add(args[index + 1]); break;
                case "--filter-not-trait": break;
                default: throw new InvalidDataException($"Lane '{lane.Name}' has unsupported MTP argument '{args[index]}'.");
            }
        }
        return (included.Count == 0 || included.Any(pattern => Matches(classFullName, pattern))) &&
               excluded.All(pattern => !Matches(classFullName, pattern));
    }

    private static bool Matches(string classFullName, string pattern) =>
        pattern.StartsWith('*') && pattern.EndsWith('*')
            ? classFullName.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase)
            : classFullName.Equals(pattern, StringComparison.Ordinal);

    private static bool ExplicitlyExcludes(AcceptanceTestLane lane, string classFullName)
    {
        var args = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (args[index] == "--filter-not-class" && Matches(classFullName, args[index + 1]))
                return true;
        }
        return false;
    }
}
