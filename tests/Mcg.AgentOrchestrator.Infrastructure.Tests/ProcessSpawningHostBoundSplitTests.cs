using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Infrastructure.Tests;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class ProcessSpawningHostBoundSplitTests
{
    private const string OriginalLaneName = "Process spawning";
    private const string HostBoundLaneName = "Process spawning host-bound";
    private const string HostBoundCollection = "ProcessSpawningHostBound";
    private const string TimeoutPolicyExclusion = "&FullyQualifiedName!~MtpTestRunnerScriptTestsTimeoutPolicy";
    private static readonly Type[] HostBoundClasses =
    [
        typeof(ConductorSelfRelaunchTests),
        typeof(ConductorSelfRelaunchTestsIsolatedArtifacts),
        typeof(ConductorSelfRelaunchTestsLandingBuildStore),
        typeof(ConductorSelfRelaunchTestsRootCheckout),
        typeof(ConductorSelfRelaunchTestsRelaunchBuildQosOptOutTests),
        typeof(ConductorSuccessorSelfCheckTests),
        typeof(DisposableTrialRootTests),
        typeof(DisposableTrialRootNegativeControlTests),
        typeof(PostLandingCanaryCaptureAvailabilityTests),
        typeof(MtpTestRunnerScriptTestsTimeoutPolicy)
    ];

    [Xunit.Fact]
    public void EveryClassHasOneLaneAndHostBoundClassesHaveOnlyTheirOwner()
    {
        var lanes = LoadLanes();
        var hostBoundLane = Xunit.Assert.Single(lanes, lane => lane.Name == HostBoundLaneName);
        Xunit.Assert.Equal([HostBoundCollection], hostBoundLane.OwnedCollections);
        var descriptors = RunnableClasses();
        Xunit.Assert.NotEmpty(descriptors);
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        foreach (var descriptor in descriptors)
        {
            var matches = AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName);
            Xunit.Assert.True(matches.Count == 1,
                $"{descriptor.FullName} selected by {matches.Count} lanes: {string.Join(", ", matches.Select(lane => lane.Name))}");
        }

        Xunit.Assert.Equal(HostBoundClasses.Select(type => type.FullName!).Order(StringComparer.Ordinal),
            SelectedClasses(resolved, descriptors, HostBoundLaneName));
        foreach (var type in HostBoundClasses)
        {
            Xunit.Assert.Equal(HostBoundCollection,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name);
            var matches = AcceptanceLaneMembership.LanesIncluding(resolved, type.FullName!);
            Xunit.Assert.Equal(HostBoundLaneName, Xunit.Assert.Single(matches).Name);
            Xunit.Assert.DoesNotContain(matches, lane => lane.Name is OriginalLaneName or "Remainder");
        }

        var definition = Xunit.Assert.IsType<Xunit.CollectionDefinitionAttribute>(
            typeof(ProcessSpawningHostBoundCollection).GetCustomAttribute<Xunit.CollectionDefinitionAttribute>());
        Xunit.Assert.Equal(HostBoundCollection, definition.Name);
        Xunit.Assert.True(definition.DisableParallelization);
        Xunit.Assert.True(typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>)
            .IsAssignableFrom(typeof(ProcessSpawningHostBoundCollection)));
    }

    [Xunit.Fact]
    public void AffectedLaneUnionEqualsPreSplitProcessSpawningSelection()
    {
        var lanes = LoadLanes();
        var originalLane = Xunit.Assert.Single(lanes, lane => lane.Name == OriginalLaneName);
        var hostBoundLane = Xunit.Assert.Single(lanes, lane => lane.Name == HostBoundLaneName);
        var descriptors = RunnableClasses();
        Xunit.Assert.NotEmpty(descriptors);
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var originalSelected = SelectedClasses(resolved, descriptors, OriginalLaneName);
        var hostBoundSelected = SelectedClasses(resolved, descriptors, HostBoundLaneName);
        Xunit.Assert.NotEmpty(originalSelected);
        Xunit.Assert.NotEmpty(hostBoundSelected);
        Xunit.Assert.Empty(originalSelected.Intersect(hostBoundSelected, StringComparer.Ordinal));

        // Reverse the new timeout exclusion as well as collection ownership to recover the old lane.
        Xunit.Assert.Contains(TimeoutPolicyExclusion, originalLane.Filter, StringComparison.Ordinal);
        var baselineLanes = lanes.Where(lane => lane.Name != HostBoundLaneName)
            .Select(lane => lane.Name == OriginalLaneName
                ? originalLane with
                {
                    Filter = originalLane.Filter.Replace(TimeoutPolicyExclusion, "", StringComparison.Ordinal) +
                        "|" + hostBoundLane.Filter
                }
                : lane).ToArray();
        var baselineDescriptors = descriptors.Select(descriptor => descriptor.Collection == HostBoundCollection
            ? descriptor with { Collection = TestCollections.ProcessSpawning }
            : descriptor).ToArray();
        var baselineResolved = AcceptanceLaneMembership.ResolveOwnedCollections(baselineLanes, baselineDescriptors);
        var baselineSelected = SelectedClasses(baselineResolved, baselineDescriptors, OriginalLaneName);
        Xunit.Assert.NotEmpty(baselineSelected);
        Xunit.Assert.Equal(baselineSelected,
            originalSelected.Union(hostBoundSelected, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void HostKeyKeepsHostBoundLaneLocalAndRemainingLaneEligible()
    {
        var lanes = LoadLanes();
        var hostBoundLane = Xunit.Assert.Single(lanes, lane => lane.Name == HostBoundLaneName);
        var originalLane = Xunit.Assert.Single(lanes, lane => lane.Name == OriginalLaneName);
        Xunit.Assert.Contains("host:ProcessSpawningHostBound", hostBoundLane.ExclusiveResourceKeys);
        Xunit.Assert.Contains("xunit:ProcessSpawning", hostBoundLane.ExclusiveResourceKeys);
        var hostBoundCheck = CreateCheck(hostBoundLane);
        var originalCheck = CreateCheck(originalLane);
        Xunit.Assert.True(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(hostBoundCheck, out _, out _));
        Xunit.Assert.True(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(originalCheck, out _, out _));
        var configuration = new RemoteLaneExecutorConfiguration(
            [new("fixture", 60)], [hostBoundCheck.Name, originalCheck.Name], null)
        {
            MachineLocalResourceKeys =
            [
                "xunit:ProcessSpawning", "xunit:EnvMutation",
                "xunit:ProcessSpawningProcessLocal", "xunit:DotnetBuildSlots"
            ]
        };
        var root = Path.Combine(Path.GetTempPath(), $"mcg-host-bound-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var executor = new FakeRemoteLaneExecutor();
            using var coordinator = new RemoteLaneCoordinator(configuration,
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
                root, null, executor, new ManualRemoteLaneClock(), TimeSpan.FromSeconds(1), null);
            Xunit.Assert.False(coordinator.IsEligible(hostBoundCheck));
            Xunit.Assert.True(coordinator.IsEligible(originalCheck));
            Xunit.Assert.Empty(executor.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ReducedEstimateEnablesFreshOfferAndOldFailuresKeepLaneLocal()
    {
        var lane = Xunit.Assert.Single(LoadLanes(), lane => lane.Name == OriginalLaneName);
        Xunit.Assert.Equal(275, lane.EstimatedSerialSeconds);
        var fresh = new RemoteLaneOfferInputs(lane.Name, null, 0, lane.EstimatedSerialSeconds,
            null, 0, 599, ConsecutiveRemoteFailures: 0);
        var decision = RemoteLaneOfferPolicy.Decide(fresh);
        Xunit.Assert.True(decision.Offer);
        Xunit.Assert.Equal("fits", decision.Reason);
        Xunit.Assert.Equal(lane.EstimatedSerialSeconds, decision.LocalSeconds);

        var old = RemoteLaneOfferPolicy.Decide(fresh with
        {
            ManifestEstimateSeconds = 435,
            ConsecutiveRemoteFailures = 2
        });
        Xunit.Assert.False(old.Offer);
        Xunit.Assert.Equal("remote-slower", old.Reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("unowned-key")]
    [Xunit.InlineData("missing-host-key")]
    [Xunit.InlineData("different-shared-key")]
    public void InvalidHostBoundSharedKeyRejectsPartition(string mutation)
    {
        var lanes = LoadLanes();
        var assembly = typeof(ProcessSpawningHostBoundSplitTests).Assembly;
        var runnableClasses = RunnableClasses()
            .Select(descriptor => assembly.GetType(descriptor.FullName, throwOnError: true)!)
            .ToArray();
        // Establish that the real partition is valid before breaking exactly one sibling constraint.
        AssertLanePartitionInvariants(new AcceptanceGateEngineSettings { InfrastructureTestLanes = lanes },
            runnableClasses);
        var mutatedLanes = lanes.Select(lane =>
            mutation == "unowned-key" && lane.Name == OriginalLaneName
                ? lane with { OwnedCollections = [] }
                : mutation == "missing-host-key" && lane.Name == HostBoundLaneName
                    ? lane with { ExclusiveResourceKeys = ["xunit:ProcessSpawning"] }
                    : mutation == "different-shared-key" && lane.Name == HostBoundLaneName
                        ? lane with { ExclusiveResourceKeys = ["xunit:EnvMutation", "host:ProcessSpawningHostBound"] }
                        : lane).ToArray();
        var error = Xunit.Assert.Throws<InvalidDataException>(() => AssertLanePartitionInvariants(
            new AcceptanceGateEngineSettings { InfrastructureTestLanes = mutatedLanes }, runnableClasses));

        Xunit.Assert.Contains($"Acceptance lane '{HostBoundLaneName}'", error.Message);
        Xunit.Assert.Contains(mutation == "different-shared-key"
            ? "exclusive resource key 'xunit:EnvMutation'"
            : "exclusive resource key 'xunit:ProcessSpawning'", error.Message);
    }

    private static void AssertLanePartitionInvariants(AcceptanceGateEngineSettings settings,
        IReadOnlyList<Type> runnableClasses)
    {
        var helper = typeof(AcceptanceGateEngineSettingsTests).GetMethod("AssertLanePartitionInvariants",
            BindingFlags.NonPublic | BindingFlags.Static);
        Xunit.Assert.NotNull(helper);
        helper.Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null,
            parameters: [settings, runnableClasses, false], culture: null);
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static AcceptanceTestClassDescriptor[] RunnableClasses() =>
        typeof(ProcessSpawningHostBoundSplitTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .Select(type => new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name))
            .ToArray();

    private static string[] SelectedClasses(IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> descriptors, string laneName) =>
        descriptors.Where(descriptor => AcceptanceLaneMembership.LanesIncluding(lanes, descriptor.FullName)
                .Any(lane => lane.Name == laneName))
            .Select(descriptor => descriptor.FullName).Order(StringComparer.Ordinal).ToArray();

    private static Check CreateCheck(AcceptanceTestLane lane) => new()
    {
        Name = $"infrastructure tests: {lane.Name}",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", lane.Filter],
        ExclusiveResourceKeys = lane.ExclusiveResourceKeys
    };
}
