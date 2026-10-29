using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class LanePlacementAssertions
{
    internal static void AssertGroup(
        IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<string> groupLaneNames,
        IReadOnlyCollection<string> partitionLaneNames,
        Type[] runnableTypes)
    {
        Xunit.Assert.NotEmpty(groupLaneNames);
        Xunit.Assert.NotEmpty(runnableTypes);
        var group = groupLaneNames.Select(name =>
        {
            var lane = Xunit.Assert.Single(lanes, lane => lane.Name == name);
            Xunit.Assert.True(partitionLaneNames.Contains(name, StringComparer.Ordinal),
                $"Lane '{name}' is missing from the manifest partition lane lists.");
            AssertClassReferences(lane, runnableTypes);
            return lane;
        }).ToArray();

        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, runnableTypes.Select(type =>
            new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name)).ToArray());
        var resolvedGroup = group.Select(lane => resolved.Single(item => item.Name == lane.Name)).ToArray();
        var remainder = Xunit.Assert.Single(resolved, lane => lane.Name == "Remainder");
        foreach (var type in runnableTypes)
        {
            var memberships = AcceptanceLaneMembership.LanesIncluding(resolvedGroup, type.FullName!);
            Xunit.Assert.True(memberships.Count <= 1,
                $"Class '{type.FullName}' appears in multiple group lanes: {string.Join(", ", memberships.Select(lane => lane.Name))}.");
            if (memberships.Count == 0)
                continue;
            Xunit.Assert.True(AcceptanceLaneMembership.LanesIncluding([remainder], type.FullName!).Count == 0,
                $"Class '{type.FullName}' appears in both '{memberships[0].Name}' and 'Remainder'; add its Remainder exclusion.");
        }
    }

    private static void AssertClassReferences(AcceptanceTestLane lane, Type[] runnableTypes)
    {
        var arguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (arguments[index] is not ("--filter-class" or "--filter-not-class"))
                continue;
            var pattern = arguments[index + 1];
            var selector = pattern.StartsWith('*') && pattern.EndsWith('*')
                ? $"FullyQualifiedName~{pattern.Trim('*')}"
                : $"FullyQualifiedName={pattern}";
            var reference = lane with { Filter = selector };
            Xunit.Assert.True(runnableTypes.Any(type =>
                    AcceptanceLaneMembership.MatchingIncludePatterns(reference, type.FullName!).Count != 0),
                $"Lane '{lane.Name}' class reference '{pattern}' matches no runnable test class.");
        }
    }
}
