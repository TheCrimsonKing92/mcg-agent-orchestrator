using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProcessSpawningCollectionSplitTests
{
    private const string ParallelCollection = "IsolatedProcessSpawning";

    private static readonly Type[] MovedClasses =
    [
        typeof(AcceptanceFailureCensusScriptTests),
        typeof(AcceptanceLaneClosureHasherTests),
        typeof(AcceptancePartitionVerdictCacheContentReuseTests),
        typeof(AssemblyTempRedirectTests),
        typeof(AssemblyTempRedirectChildSmokeTests),
        typeof(CitedPriorEvidenceResolverTests),
        typeof(CliHelpTests),
        typeof(ConductorDriverTests),
        typeof(ExcessWorkerRoundAnalysisScriptTests),
        typeof(GitCliTests),
        typeof(LaneTimingMeasurementScriptTests),
        typeof(MtpManagedFilterDiscoveryTests),
        typeof(MtpNoBuildReceiptIdentityTests),
        typeof(OrchestratorSnapshotStatusTimeoutTests),
        typeof(SemanticAcceptanceTests),
        typeof(WorkerContextArtifactsCharacterizationTests),
        typeof(WorkerDispatchTestsModelSelection)
    ];

    private static readonly Type[] SerialClasses =
    [
        typeof(ConductorSelfRelaunchTests),
        typeof(ConductorSuccessorSelfCheckTests),
        typeof(DispatchProcessHostTests),
        typeof(GoalWorktreeIsolatedDotnetTests),
        typeof(IcaclsIntegrityLabelerTests),
        typeof(LauncherScriptTests),
        typeof(MtpTestRunnerScriptTests),
        typeof(OwnedProcessExitObservationTests),
        typeof(PostLandingCanaryCaptureAvailabilityTests),
        typeof(ProcessTreeGuiSuppressionTests),
        typeof(ConsoleIoPreservationTests),
        typeof(RunGoalServiceProcessContractTests)
    ];

    [Xunit.Fact]
    public void MovedFactsHaveOnlyTheParallelLaneAndNewDeclaringClasses()
    {
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot())
            .InfrastructureTestLanes;
        var descriptors = typeof(ProcessSpawningCollectionSplitTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .Select(type => new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name))
            .ToArray();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        foreach (var type in new[]
                 {
                     typeof(OrchestratorSnapshotStatusTimeoutTests),
                     typeof(MtpManagedFilterDiscoveryTests),
                     typeof(MtpNoBuildReceiptIdentityTests)
                 })
        {
            Xunit.Assert.Equal(ParallelCollection,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name);
            var selected = AcceptanceLaneMembership.LanesIncluding(resolved, type.FullName!);
            Xunit.Assert.Equal("Process spawning parallel", Xunit.Assert.Single(selected).Name);
            Xunit.Assert.DoesNotContain(selected, lane => lane.Name is "Process spawning" or "Remainder");
        }

        foreach (var (oldType, newType, methodName) in new[]
                 {
                     (typeof(LauncherScriptTests), typeof(OrchestratorSnapshotStatusTimeoutTests),
                         "GetOrchestratorSnapshotStatusTimeoutKillsOwnedStatusProcessAndReportsPartialData"),
                     (typeof(MtpTestRunnerScriptTests), typeof(MtpManagedFilterDiscoveryTests),
                         "ManifestFilters_TranslateAndKeepExclusionSemantics"),
                     (typeof(MtpTestRunnerScriptTests), typeof(MtpManagedFilterDiscoveryTests),
                         "BooleanFilters_SelectExactManagedTestSets")
                 })
        {
            Xunit.Assert.Null(oldType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            Xunit.Assert.NotNull(newType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        }

        var snapshotFact = typeof(OrchestratorSnapshotStatusTimeoutTests)
            .GetMethod("GetOrchestratorSnapshotStatusTimeoutKillsOwnedStatusProcessAndReportsPartialData")!
            .GetCustomAttribute<Xunit.FactAttribute>();
        Xunit.Assert.Equal("GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data",
            snapshotFact?.DisplayName);
    }

    [Xunit.Fact(DisplayName = "ProcessSpawning_collection_split_preserves_serial_hazards_and_removes_exclusive_overlap")]
    public void ProcessSpawningCollectionSplitPreservesSerialHazardsAndRemovesExclusiveOverlap()
    {
        var serialDefinition = Xunit.Assert.IsType<Xunit.CollectionDefinitionAttribute>(
            typeof(ProcessSpawningCollection).GetCustomAttribute<Xunit.CollectionDefinitionAttribute>());
        Xunit.Assert.Equal(TestCollections.ProcessSpawning, serialDefinition.Name);
        Xunit.Assert.True(serialDefinition.DisableParallelization);
        Xunit.Assert.True(
            typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>)
                .IsAssignableFrom(typeof(ProcessSpawningCollection)));

        var parallelDefinition = Xunit.Assert.IsType<Xunit.CollectionDefinitionAttribute>(
            typeof(IsolatedProcessSpawningCollection).GetCustomAttribute<Xunit.CollectionDefinitionAttribute>());
        Xunit.Assert.Equal(ParallelCollection, parallelDefinition.Name);
        Xunit.Assert.False(parallelDefinition.DisableParallelization);
        Xunit.Assert.True(
            typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>)
                .IsAssignableFrom(typeof(IsolatedProcessSpawningCollection)));

        Xunit.Assert.All(MovedClasses, type =>
            Xunit.Assert.Equal(
                ParallelCollection,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name));
        Xunit.Assert.All(SerialClasses, type =>
            Xunit.Assert.Equal(
                TestCollections.ProcessSpawning,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name));

        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());
        var serialLane = Xunit.Assert.Single(settings.InfrastructureTestLanes
            .Where(lane => lane.Name == "Process spawning"));
        Xunit.Assert.Equal(["xunit:ProcessSpawning"], serialLane.ExclusiveResourceKeys);
        Xunit.Assert.All(SerialClasses, type =>
            Xunit.Assert.Contains($"FullyQualifiedName~{type.Name}", serialLane.Filter, StringComparison.Ordinal));
        Xunit.Assert.All(MovedClasses, type =>
            Xunit.Assert.DoesNotContain($"FullyQualifiedName~{type.Name}", serialLane.Filter, StringComparison.Ordinal));

        var parallelLane = Xunit.Assert.Single(settings.InfrastructureTestLanes
            .Where(lane => lane.Name == "Process spawning parallel"));
        Xunit.Assert.Empty(parallelLane.ExclusiveResourceKeys);
        Xunit.Assert.All(MovedClasses, type =>
            Xunit.Assert.Contains($"FullyQualifiedName~{type.Name}", parallelLane.Filter, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "WorkerDispatchFixtures_lane_split_preserves_original_entries_and_declares_additions")]
    public void WorkerDispatchFixturesLaneSplitPreservesOriginalEntriesAndDeclaresAdditions()
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());
        var shardEntries = WorkerDispatchFixtureLaneNames
            .Select(name => (
                Lane: name,
                Entries: FilterClassEntries(Xunit.Assert.Single(settings.InfrastructureTestLanes
                    .Where(lane => lane.Name == name)).Filter)))
            .ToArray();

        Xunit.Assert.DoesNotContain(
            settings.InfrastructureTestLanes,
            lane => lane.Name == "Worker dispatch fixtures");
        var allEntries = shardEntries.SelectMany(shard => shard.Entries).ToArray();
        Xunit.Assert.Equal(
            OriginalWorkerDispatchFixtureClasses.Order(StringComparer.Ordinal),
            allEntries.Where(entry => OriginalWorkerDispatchFixtureClasses.Contains(entry, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal));
        Xunit.Assert.Equal(
            AdditionalWorkerDispatchFixtureClasses.Order(StringComparer.Ordinal),
            allEntries.Where(entry => !OriginalWorkerDispatchFixtureClasses.Contains(entry, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal));
        foreach (var left in shardEntries)
        {
            foreach (var right in shardEntries.Where(other =>
                         !string.Equals(other.Lane, left.Lane, StringComparison.Ordinal)))
            {
                var shared = left.Entries.Intersect(right.Entries, StringComparer.Ordinal).ToArray();
                Xunit.Assert.True(
                    shared.Length == 0,
                    $"Lanes '{left.Lane}' and '{right.Lane}' both claim [{string.Join(", ", shared)}].");
            }
        }
    }

    private static readonly string[] WorkerDispatchFixtureLaneNames =
    [
        "Worker dispatch fixtures A",
        "Worker dispatch fixtures B",
        "Worker dispatch fixtures C"
    ];

    private static readonly string[] OriginalWorkerDispatchFixtureClasses =
    [
        "RealWorkerProcessGuardTests",
        "WorkerDispatchTestsDispatchPreparation",
        "WorkerDispatchTestsModelSelectionEnvMutation",
        "WorkerDispatchTestsSandboxLowIntegrity",
        "WorkerDispatchTestsSubscriptionPreflight",
        "WorkerDispatchTestsWorkerResultClassification",
        "WorkerProcessJobsTests"
    ];

    private static readonly string[] AdditionalWorkerDispatchFixtureClasses =
    [
        "DispatchHostLifetimeHandoffTests"
    ];

    private static string[] FilterClassEntries(string filter) =>
        filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(operand => operand["FullyQualifiedName~".Length..])
            .ToArray();
}
