using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads the checked-in manifest and modifies only in-memory lane copies.
public sealed class RemainderTailLaneMoveTests
{
    private const string Remainder = "Remainder";
    private const string BalanceB = "Remainder balance B";
    private const string Inclusion = "|FullyQualifiedName~AcceptanceExecutionCacheIsolatedBuildTests";
    private const string Exclusion = "&FullyQualifiedName!~AcceptanceExecutionCacheIsolatedBuildTests";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedManifest_SelectsIsolatedBuildClassOnlyInBalanceB(bool resolveCollections)
    {
        var lanes = LoadLanes();
        if (resolveCollections) lanes = Resolve(lanes, RunnableTypes());

        Assert.Equal(27, lanes.Count);
        Assert.Equal(BalanceB, Assert.Single(AcceptanceLaneMembership.LanesIncluding(
            lanes, typeof(AcceptanceExecutionCacheIsolatedBuildTests).FullName!)).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeforeMoveComparison_ChangesOnlyIsolatedBuildClass(bool resolveCollections)
    {
        var loaded = LoadLanes();
        var beforeMove = BeforeMove(loaded);
        var types = RunnableTypes();
        var moved = typeof(AcceptanceExecutionCacheIsolatedBuildTests);
        Assert.Contains(moved, types);
        if (resolveCollections)
        {
            loaded = Resolve(loaded, types);
            beforeMove = Resolve(beforeMove, types);
        }

        Assert.Equal(27, loaded.Count);
        Assert.Equal(27, beforeMove.Count);
        Assert.Equal(new[] { Remainder }, LaneNames(beforeMove, moved));
        Assert.Equal(new[] { BalanceB }, LaneNames(loaded, moved));
        foreach (var type in types.Where(type => type != moved))
            Assert.Equal(LaneNames(beforeMove, type), LaneNames(loaded, type));
    }

    private static IReadOnlyList<AcceptanceTestLane> BeforeMove(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        var balanceB = Assert.Single(lanes, lane => lane.Name == BalanceB);
        var remainder = Assert.Single(lanes, lane => lane.Name == Remainder);
        Assert.Equal(2, balanceB.Filter.Split(Inclusion, StringSplitOptions.None).Length);
        Assert.Equal(2, remainder.Filter.Split(Exclusion, StringSplitOptions.None).Length);
        return lanes.Select(lane => lane.Name switch
        {
            BalanceB => lane with { Filter = lane.Filter.Replace(Inclusion, "", StringComparison.Ordinal) },
            Remainder => lane with { Filter = lane.Filter.Replace(Exclusion, "", StringComparison.Ordinal) },
            _ => lane
        }).ToArray();
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static Type[] RunnableTypes() =>
        typeof(RemainderTailLaneMoveTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<AcceptanceTestLane> Resolve(
        IReadOnlyList<AcceptanceTestLane> lanes, Type[] types) =>
        AcceptanceLaneMembership.ResolveOwnedCollections(lanes, types.Select(type =>
            new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name)).ToArray());

    private static string[] LaneNames(IReadOnlyList<AcceptanceTestLane> lanes, Type type) =>
        AcceptanceLaneMembership.LanesIncluding(lanes, type.FullName!)
            .Select(lane => lane.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
