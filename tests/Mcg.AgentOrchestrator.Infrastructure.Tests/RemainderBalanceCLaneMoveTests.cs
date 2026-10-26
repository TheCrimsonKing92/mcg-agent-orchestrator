using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads the checked-in manifest and modifies only in-memory lane copies.
public sealed class RemainderBalanceCLaneMoveTests
{
    private const string Remainder = "Remainder";
    private const string BalanceB = "Remainder balance B";
    private const string BalanceC = "Remainder balance C";
    private static readonly string[] Substrings =
    [
        "WorkerBuildRequest",
        "ExitReconcileBuild",
        "CompletionBuild",
        "WorkerBuildScopeDispatch",
        "FocusedEvidenceUnionPlanning",
        "MergeTrainDeferralFixtureEquivalence",
        "MergeTrainRedAttributionReplayScenario",
        "LifecycleGoalEpicInheritance",
        "SourceSizeRatchetClassRetightener",
        "ProviderUsageSelfHeal",
        "EpicPlanSourceFacts",
        "EpicDetailReadSnapshot",
        "RemoteWorkerBuildCheckFallback",
        "DeveloperCascade",
        "WorkerBuildReceipt",
        "ProviderHoldAcrossRestart",
        "ProviderUsageHoldSelfRetry",
        "ProviderSignInFailover",
        "MainIntegrationSquash",
        "ProjectIntegrationBranch",
        "TerminalGoalSweepTerminalTaskClosure",
        "RelaunchDrainV1GroupedGate",
        "AttributedFailureAutoRoute",
        "AgentRosterReload"
    ];
    private static readonly string[] MovedClassNames =
    [
        "WorkerBuildRequestScopeBindingTests",
        "WorkerBuildRequestScopeOwnershipTests",
        "WorkerBuildRequestIntegrationTests",
        "WorkerBuildRequestAuthorityTests",
        "WorkerBuildRequestRecoveryTests",
        "ExitReconcileBuildControlTests",
        "ExitReconcileBuildCompositionTests",
        "ExitReconcileBuildDispositionTests",
        "CompletionBuildOriginPredicateTests",
        "CompletionBuildEvidenceLaunchTests",
        "CompletionBuildEvidenceRecoveryTests",
        "CompletionBuildEvidenceIdentityTests",
        "CompletionBuildEvidenceLifecycleTests",
        "TaskCompletionBuildEvidencePersistenceTests",
        "WorkerBuildScopeDispatchBindingTests",
        "WorkerBuildScopeDispatchOwnershipTests",
        "WorkerBuildScopeDispatchPersistenceTests",
        "FocusedEvidenceUnionPlanningTests",
        "MergeTrainDeferralFixtureEquivalenceTests",
        "MergeTrainRedAttributionReplayScenarioTests",
        "LifecycleGoalEpicInheritanceTests",
        "SourceSizeRatchetClassRetightenerTests",
        "ProviderUsageSelfHealScenarioTests",
        "EpicPlanSourceFactsTests",
        "EpicDetailReadSnapshotTests",
        "RemoteWorkerBuildCheckFallbackTests",
        "DeveloperCascadeDispatchTests",
        "DeveloperCascadeFixtureEquivalenceTests",
        "WorkerBuildReceiptTestsStableScope",
        "WorkerBuildReceiptGateTests",
        "WorkerBuildReceiptCoverageTests",
        "WorkerBuildReceiptDigestDetailTests",
        "ProviderHoldAcrossRestartScenarioTests",
        "ProviderUsageHoldSelfRetryScenarioTests",
        "ProviderSignInFailoverScenarioTests",
        "ProviderSignInFailoverTests",
        "MainIntegrationSquashTests",
        "ProjectIntegrationBranchTests",
        "TerminalGoalSweepTerminalTaskClosureTests",
        "RelaunchDrainV1GroupedGateScenarioTests",
        "AttributedFailureAutoRouteScenarioTests",
        "AttributedFailureAutoRouteControlTests",
        "AgentRosterReloadScenarioTests",
        "AgentRosterReloaderTests",
        "AgentRosterReloadWiringBoundaryTests"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedManifest_SelectsEveryMovedClassOnlyInBalanceC(bool resolveCollections)
    {
        var lanes = LoadLanes();
        var types = RunnableTypes();
        if (resolveCollections) lanes = Resolve(lanes, types);

        Assert.Equal(27, lanes.Count);
        foreach (var name in MovedClassNames)
        {
            var type = Assert.Single(types, type => type.Name == name);
            Assert.Equal(new[] { BalanceC }, LaneNames(lanes, type));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeforeMoveComparison_ChangesOnlyTheMovedClasses(bool resolveCollections)
    {
        var loaded = LoadLanes();
        var beforeMove = BeforeMove(loaded);
        var types = RunnableTypes();
        var moved = MovedClassNames.Select(name => Assert.Single(types, type => type.Name == name)).ToHashSet();
        if (resolveCollections)
        {
            loaded = Resolve(loaded, types);
            beforeMove = Resolve(beforeMove, types);
        }

        Assert.Equal(27, loaded.Count);
        Assert.Equal(26, beforeMove.Count);
        foreach (var type in moved)
        {
            Assert.Equal(new[] { Remainder }, LaneNames(beforeMove, type));
            Assert.Equal(new[] { BalanceC }, LaneNames(loaded, type));
        }
        foreach (var type in types.Where(type => !moved.Contains(type)))
            Assert.Equal(LaneNames(beforeMove, type), LaneNames(loaded, type));
    }

    [Fact]
    public void NegativeControls_DetectDoubleAndMissingMembership()
    {
        var loaded = LoadLanes();
        var moved = Assert.Single(RunnableTypes(), type => type.Name == "ExitReconcileBuildControlTests");
        const string exclusion = "&FullyQualifiedName!~ExitReconcileBuild";
        const string inclusion = "|FullyQualifiedName~ExitReconcileBuild";
        var remainder = Assert.Single(loaded, lane => lane.Name == Remainder);
        var balanceC = Assert.Single(loaded, lane => lane.Name == BalanceC);
        Assert.Equal(2, remainder.Filter.Split(exclusion, StringSplitOptions.None).Length);
        Assert.Equal(2, balanceC.Filter.Split(inclusion, StringSplitOptions.None).Length);

        var doubleMembership = loaded.Select(lane => lane.Name == Remainder
            ? lane with { Filter = lane.Filter.Replace(exclusion, "", StringComparison.Ordinal) }
            : lane).ToArray();
        Assert.Equal(new[] { Remainder, BalanceC }, LaneNames(doubleMembership, moved));

        var missingMembership = loaded.Select(lane => lane.Name == BalanceC
            ? lane with { Filter = lane.Filter.Replace(inclusion, "", StringComparison.Ordinal) }
            : lane).ToArray();
        Assert.Empty(AcceptanceLaneMembership.LanesIncluding(missingMembership, moved.FullName!));
        Assert.Equal(Remainder, Assert.Single(AcceptanceLaneMembership.LanesIncluding(
            loaded, "QuokkaHarborUnlistedProbeTests")).Name);
    }

    [Fact]
    public void ManifestShape_NewLaneIsKeylessPartitionedAndEstimated()
    {
        var lanes = LoadLanes();
        var balanceC = Assert.Single(lanes, lane => lane.Name == BalanceC);
        var names = lanes.Select(lane => lane.Name).ToArray();
        Assert.Equal(Array.IndexOf(names, BalanceB) + 1, Array.IndexOf(names, BalanceC));
        Assert.Equal(620.0, balanceC.EstimatedSerialSeconds);
        Assert.Empty(balanceC.ExclusiveResourceKeys);
        Assert.Empty(balanceC.OwnedCollections);
        Assert.Equal(415.6, Assert.Single(lanes, lane => lane.Name == Remainder).EstimatedSerialSeconds);
        Assert.Equal(string.Join("|", Substrings.Select(substring => $"FullyQualifiedName~{substring}")),
            balanceC.Filter);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(), "config", "acceptance-manifest.json")));
        var partition = Assert.Single(manifest.RootElement.GetProperty("engine")
            .GetProperty("localTestPartitions").EnumerateArray(),
            partition => partition.GetProperty("name").GetString() == Remainder);
        var partitionNames = partition.GetProperty("laneNames").EnumerateArray()
            .Select(name => name.GetString()).ToArray();
        Assert.Contains(BalanceC, partitionNames);
        Assert.Equal(Array.IndexOf(partitionNames, BalanceB) + 1, Array.IndexOf(partitionNames, BalanceC));
    }

    private static IReadOnlyList<AcceptanceTestLane> BeforeMove(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        Assert.Single(lanes, lane => lane.Name == BalanceC);
        var remainder = Assert.Single(lanes, lane => lane.Name == Remainder);
        var exclusions = string.Concat(Substrings.Select(substring => $"&FullyQualifiedName!~{substring}"));
        Assert.Equal(2, remainder.Filter.Split(exclusions + "&Category!=HostIntegration",
            StringSplitOptions.None).Length);
        return lanes.Where(lane => lane.Name != BalanceC).Select(lane => lane.Name == Remainder
            ? lane with { Filter = lane.Filter.Replace(exclusions, "", StringComparison.Ordinal) }
            : lane).ToArray();
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static Type[] RunnableTypes() =>
        typeof(RemainderBalanceCLaneMoveTests).Assembly.GetTypes()
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
