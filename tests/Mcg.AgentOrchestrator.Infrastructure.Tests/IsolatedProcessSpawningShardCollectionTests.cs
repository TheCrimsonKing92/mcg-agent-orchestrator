using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: only reads collection metadata and the checked-in acceptance manifest.
public sealed class IsolatedProcessSpawningShardCollectionTests
{
    private const string OriginalCollection = "IsolatedProcessSpawning";
    private const string LaneName = "Process spawning parallel";
    private const string BaselineFilter = "FullyQualifiedName~AcceptanceFailureCensusScriptTests|FullyQualifiedName~AcceptanceLaneClosureHasherTests|FullyQualifiedName~AcceptancePartitionVerdictCacheContentReuseTests|FullyQualifiedName~AssemblyTempRedirectTests|FullyQualifiedName~AssemblyTempRedirectChildSmokeTests|FullyQualifiedName~CitedPriorEvidenceResolverTests|FullyQualifiedName~CliHelpTests|FullyQualifiedName~ConductorDriverTests|FullyQualifiedName~ExcessWorkerRoundAnalysisScriptTests|FullyQualifiedName~GitCliTests|FullyQualifiedName~LaneTimingMeasurementScriptTests|FullyQualifiedName~SemanticAcceptanceTests|FullyQualifiedName~WorkerContextArtifactsCharacterizationTests|(FullyQualifiedName~WorkerDispatchTestsModelSelection&FullyQualifiedName!~WorkerDispatchTestsModelSelectionEnvMutation)|FullyQualifiedName~MtpNoBuildReceiptIdentityTests|FullyQualifiedName~OrchestratorSnapshotStatusTimeoutTests|FullyQualifiedName~MtpManagedFilterDiscoveryTests";

    private static readonly IReadOnlyDictionary<string, Type[]> Shards = new Dictionary<string, Type[]>(StringComparer.Ordinal)
    {
        [IsolatedProcessSpawningShardCollections.ShardA] = [typeof(ConductorDriverTestsFindingEvidenceReuse)],
        [IsolatedProcessSpawningShardCollections.ShardB] = [typeof(MtpManagedFilterDiscoveryTests), typeof(MtpNoBuildReceiptIdentityTests)],
        [IsolatedProcessSpawningShardCollections.ShardC] = [typeof(WorkerDispatchTestsModelSelection), typeof(CliHelpTests), typeof(ExcessWorkerRoundAnalysisScriptTests)]
    };

    [Xunit.Fact]
    public void ShardsHaveParallelIsolatedDefinitionsAndExactMembers()
    {
        var assembly = typeof(IsolatedProcessSpawningShardCollectionTests).Assembly;
        var types = assembly.GetTypes();
        var definitions = types.Select(type => (Type: type,
                Definition: type.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>()))
            .Where(item => item.Definition?.Name?.StartsWith("IsolatedProcessSpawningShard", StringComparison.Ordinal) == true)
            .ToArray();

        Xunit.Assert.Equal(3, definitions.Length);
        Xunit.Assert.Equal(Shards.Keys.Order(StringComparer.Ordinal),
            definitions.Select(item => item.Definition!.Name!).Order(StringComparer.Ordinal));
        foreach (var (type, definition) in definitions)
        {
            Xunit.Assert.True(type.IsPublic && type.IsSealed);
            Xunit.Assert.False(definition!.DisableParallelization);
            Xunit.Assert.True(typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>).IsAssignableFrom(type));
            Xunit.Assert.Null(type.GetCustomAttribute<ProcessLocalTestCollectionAttribute>());
        }

        foreach (var (name, expected) in Shards)
        {
            var members = types.Where(type => DeclaredCollection(type) == name).ToArray();
            Xunit.Assert.Equal(expected.Select(type => type.FullName).Order(StringComparer.Ordinal),
                members.Select(type => type.FullName).Order(StringComparer.Ordinal));
            Xunit.Assert.Contains(name, LaneIsolatedRootScanner.IsolatedCollections(assembly));
        }
        Xunit.Assert.Contains(OriginalCollection, LaneIsolatedRootScanner.IsolatedCollections(assembly));
    }

    [Xunit.Fact]
    public void ShardMembersRemainInExactlyTheProcessSpawningParallelLane()
    {
        var lanes = LoadLanes();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, Descriptors(RunnableTypes()));
        foreach (var type in Shards.Values.SelectMany(types => types))
        {
            Xunit.Assert.Equal(LaneName,
                Xunit.Assert.Single(AcceptanceLaneMembership.LanesIncluding(resolved, type.FullName!)).Name);
        }
    }

    [Xunit.Fact]
    public void ShardMovePreservesBaselineFilterAndEveryClassLaneMembership()
    {
        var lanes = LoadLanes();
        var lane = Xunit.Assert.Single(lanes, item => item.Name == LaneName);
        Xunit.Assert.Equal(BaselineFilter, lane.Filter, StringComparer.Ordinal);
        Xunit.Assert.Empty(lane.ExclusiveResourceKeys);
        Xunit.Assert.Empty(lane.OwnedCollections);

        var types = RunnableTypes();
        Xunit.Assert.NotEmpty(types);
        var actual = Descriptors(types);
        var baseline = actual.Select(item => Shards.ContainsKey(item.Collection ?? "")
            ? item with { Collection = OriginalCollection }
            : item).ToArray();
        var resolvedActual = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, actual);
        var resolvedBaseline = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, baseline);
        foreach (var type in types)
        {
            var actualNames = LaneNames(resolvedActual, type);
            var baselineNames = LaneNames(resolvedBaseline, type);
            Xunit.Assert.True(actualNames.SequenceEqual(baselineNames, StringComparer.Ordinal),
                $"{type.FullName}: actual={{{string.Join(", ", actualNames)}}}, baseline={{{string.Join(", ", baselineNames)}}}");
        }
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static Type[] RunnableTypes() =>
        typeof(IsolatedProcessSpawningShardCollectionTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(method => method.GetCustomAttributes(inherit: true).Any(attribute => attribute is Xunit.FactAttribute)))
            .ToArray();

    private static string? DeclaredCollection(Type type) =>
        type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name;

    private static AcceptanceTestClassDescriptor[] Descriptors(Type[] types) =>
        types.Select(type => new AcceptanceTestClassDescriptor(type.FullName!, DeclaredCollection(type))).ToArray();

    private static string[] LaneNames(IReadOnlyList<AcceptanceTestLane> lanes, Type type) =>
        AcceptanceLaneMembership.LanesIncluding(lanes, type.FullName!)
            .Select(lane => lane.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
