using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads the checked-in manifest and modifies only in-memory lane copies.
public sealed class RemainderBalanceALaneMoveTests
{
    private const string Remainder = "Remainder";
    private const string BalanceA = "Remainder balance A";
    private static readonly string[] MovedClassNames =
    [
        "MergeTrainGateOnlySlotsBusyDeferralTests",
        "GoalWorktreeTestsRebaseAdditiveConflictRefusal",
        "IntegrationMergeChangedPathsTests",
        "GoalWorktreeTestsRebaseAdditiveConflictMerge",
        "ConductorConflictIntegrationValidateTests",
        "MergeTrainLaunchTickScenarioTests",
        "AcceptanceCohortSlotsBusyDeferralTests",
        "WorkerBuildProjectScopeSquashedBranchTests",
        "FailedRoundCheckpointPreDispatchTests",
        "ProviderUsageHoldEventsScenarioTests",
        "RemoteExecutorAdmissionConsumersTests",
        "ProviderHoldFallbackScenarioTests",
        "AcceptanceRunExecutionContextTestsFollowerPinnedBase",
        "WorkerDispatchTestsSeededRepositoryFactoryTests",
        "PendingStoreSetupCliWriteTests",
        "ConductorEvidenceAttemptLifecycleTests",
        "WorkerBuildProjectScopeTests",
        "MergeTrainMainIntegrationScenarioTests",
        "ConflictIntegrationScenarioTests",
        "IntegrationMergeDispatchCompletionScenarioTests",
        "RemoteLaneCoordinatorTestsQueuedLiveness",
        "OrchestratorWorktreeGitExcludeTests",
        "SubscriptionVerificationCompletionTests",
        "FailedRoundCheckpointReceiptRecordingTests",
        "ConductorConflictIntegrationRecordTests",
        "WorkerWorkflowBrokerRoleDeliveryTests",
        "WorkerDispatchTestsSeededRepositoryFactoryLaunchCountIsolation",
        "ConductorConflictIntegrationAdmissionTests",
        "WorkerContextRendererDispatchPathTests",
        "PassedCohortReceiptPreTrainLandingTests",
        "GoalWorktreeTestsGitHelperDiagnostics",
        "AcceptanceCohortBriefIdentityTests",
        "FailureTriageDecisionTests",
        "AcceptanceCohortMergeTrainSlotsBusyDeferralTests",
        "GoalWorktreeTestsGitCommitLaunchCount",
        "GroupedGateV1SuccessorAdoptionScenarioTests",
        "ConductorAcceptanceCohortAttributionTestsFailingTestIdentity",
        "RatchetClassCeilingRemeasureTests",
        "RatchetConflictDispatchIntegrationTests",
        "InterruptedWorkCheckpointAuthorizerTests"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedManifest_SelectsMovedAndExistingClassesOnlyInBalanceA(bool resolveCollections)
    {
        var lanes = LoadLanes();
        var types = RunnableTypes();
        if (resolveCollections) lanes = Resolve(lanes, types);

        Assert.Equal(28, lanes.Count);
        foreach (var name in MovedClassNames)
        {
            var type = Assert.Single(types, type => type.Name == name);
            Assert.Null(type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true));
            Assert.Equal(new[] { BalanceA }, LaneNames(lanes, type));
        }
        var existing = Assert.Single(types, type => type.Name == "RunEventStoreTests");
        Assert.Equal(new[] { BalanceA }, LaneNames(lanes, existing));
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

        Assert.Equal(28, loaded.Count);
        Assert.Equal(28, beforeMove.Count);
        foreach (var type in moved)
        {
            Assert.Equal(new[] { Remainder }, LaneNames(beforeMove, type));
            Assert.Equal(new[] { BalanceA }, LaneNames(loaded, type));
        }
        foreach (var type in types.Where(type => !moved.Contains(type)))
            Assert.Equal(LaneNames(beforeMove, type), LaneNames(loaded, type));
    }

    [Fact]
    public void NegativeControls_DetectDoubleAndMissingMembership()
    {
        var loaded = LoadLanes();
        var moved = Assert.Single(RunnableTypes(), type => type.Name == "IntegrationMergeChangedPathsTests");
        const string exclusion = "&FullyQualifiedName!~IntegrationMergeChangedPathsTests";
        const string inclusion = "|FullyQualifiedName~IntegrationMergeChangedPathsTests";
        var remainder = Assert.Single(loaded, lane => lane.Name == Remainder);
        var balanceA = Assert.Single(loaded, lane => lane.Name == BalanceA);
        Assert.Equal(2, remainder.Filter.Split(exclusion, StringSplitOptions.None).Length);
        Assert.Equal(2, balanceA.Filter.Split(inclusion, StringSplitOptions.None).Length);

        var doubleMembership = loaded.Select(lane => lane.Name == Remainder
            ? lane with { Filter = lane.Filter.Replace(exclusion, "", StringComparison.Ordinal) }
            : lane).ToArray();
        Assert.Equal(new[] { Remainder, BalanceA }, LaneNames(doubleMembership, moved));

        var missingMembership = loaded.Select(lane => lane.Name == BalanceA
            ? lane with { Filter = lane.Filter.Replace(inclusion, "", StringComparison.Ordinal) }
            : lane).ToArray();
        Assert.Empty(AcceptanceLaneMembership.LanesIncluding(missingMembership, moved.FullName!));
        Assert.Equal(Remainder, Assert.Single(AcceptanceLaneMembership.LanesIncluding(
            loaded, "QuokkaHarborUnlistedProbeTests")).Name);
    }

    [Fact]
    public void ManifestShape_PreservesOrderedBlocksAndEstimates()
    {
        var lanes = LoadLanes();
        var balanceA = Assert.Single(lanes, lane => lane.Name == BalanceA);
        var remainder = Assert.Single(lanes, lane => lane.Name == Remainder);
        Assert.Equal(40, MovedClassNames.Length);
        Assert.Equal("FullyQualifiedName~RunEventStoreTests" + InclusionBlock(), balanceA.Filter);
        Assert.Equal(371.4, balanceA.EstimatedSerialSeconds);
        Assert.Equal(new[] { "machine:GitHeavyLanes" }, balanceA.ExclusiveResourceKeys);
        Assert.Empty(balanceA.OwnedCollections);
        Assert.Equal(415.6, remainder.EstimatedSerialSeconds);
        var exclusions = ExclusionBlock();
        Assert.Equal(2, remainder.Filter.Split(exclusions, StringSplitOptions.None).Length);
        Assert.Equal(2, remainder.Filter.Split("&FullyQualifiedName!~RunEventStoreTests" + exclusions,
            StringSplitOptions.None).Length);
    }

    private static string InclusionBlock() =>
        string.Concat(MovedClassNames.Select(name => $"|FullyQualifiedName~{name}"));

    private static string ExclusionBlock() =>
        string.Concat(MovedClassNames.Select(name => $"&FullyQualifiedName!~{name}"));

    private static IReadOnlyList<AcceptanceTestLane> BeforeMove(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        var balanceA = Assert.Single(lanes, lane => lane.Name == BalanceA);
        var remainder = Assert.Single(lanes, lane => lane.Name == Remainder);
        var inclusions = InclusionBlock();
        var exclusions = ExclusionBlock();
        Assert.Equal(2, balanceA.Filter.Split(inclusions, StringSplitOptions.None).Length);
        Assert.Equal(2, remainder.Filter.Split(exclusions, StringSplitOptions.None).Length);
        return lanes.Select(lane => lane.Name == BalanceA
            ? lane with { Filter = lane.Filter.Replace(inclusions, "", StringComparison.Ordinal) }
            : lane.Name == Remainder
                ? lane with { Filter = lane.Filter.Replace(exclusions, "", StringComparison.Ordinal) }
                : lane).ToArray();
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static Type[] RunnableTypes() =>
        typeof(RemainderBalanceALaneMoveTests).Assembly.GetTypes()
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
