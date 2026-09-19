using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class AcceptanceGateEngineSettingsTests
{
    private GoalAcceptanceVerifierTestOverrides TestOverrides { get; } = new();

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_checked_in_manifest_splits_heavy_lanes_without_narrowing_coverage")]
    public void AcceptanceGateEngineCheckedInManifestSplitsHeavyLanesWithoutNarrowingCoverage()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(repositoryRoot);
        var startupContract = GoalAcceptanceVerifier.ValidateStartupContract(repositoryRoot);

        Xunit.Assert.Equal(4, settings.MaxConcurrentShards);
        Xunit.Assert.Equal(5, settings.PartitionVerdictFullRerunEveryN);
        Xunit.Assert.Equal(AcceptanceGateEngineSettings.DefaultOutputCaptureLimitBytes, settings.OutputCaptureLimitBytes);
        Xunit.Assert.Equal(22, settings.InfrastructureTestLanes.Count);
        Xunit.Assert.Equal(8, startupContract.ManifestCheckCount);
        using var manifestDocument = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "config", "acceptance-manifest.json")));
        var manifestLanes = manifestDocument.RootElement
            .GetProperty("engine")
            .GetProperty("infrastructureTestLanes")
            .EnumerateArray()
            .ToArray();
        Xunit.Assert.Equal(manifestLanes.Length, settings.InfrastructureTestLanes.Count);
        Xunit.Assert.Equal(
            manifestLanes.Select(lane => lane.GetProperty("name").GetString()),
            settings.InfrastructureTestLanes.Select(lane => lane.Name));
        Xunit.Assert.All(
            manifestLanes,
            lane => Xunit.Assert.True(
                lane.TryGetProperty("estimatedSerialSeconds", out _),
                $"Checked-in lane '{lane.GetProperty("name").GetString()}' must explicitly declare a serial-duration estimate."));
        Xunit.Assert.All(
            settings.InfrastructureTestLanes,
            lane => Xunit.Assert.True(
                lane.EstimatedSerialSeconds > 0 && double.IsFinite(lane.EstimatedSerialSeconds),
                $"Checked-in lane '{lane.Name}' must carry a positive finite serial-duration estimate."));
        AssertLaneSetPreservesCoverage(
            settings,
            ["Goal lifecycle commands", "Goal worktree cleanup", "Goal worktree parallel"],
            [
                "AcceptanceCohortWorkflowTests",
                "AcceptanceVerdictCarryForwardTests",
                "CliCommandTestsGoalLifecycleCleanupHooks",
                "CliCommandTestsPersistentRunnerCommands",
                "CliPersistentStateRunnerAcceptanceFailureRecoveryTests",
                "CliCommandTestsSubscriptionDispatchCommands",
                "GoalGitFactIndexTests",
                "GoalsPruneTests",
                "GoalWorktreeTestsAcceptanceLanding",
                "GoalWorktreeTestsCleanupHookDelegates",
                "GoalWorktreeTestsCreationResolution",
                "GoalWorktreeTestsOrphanEphemeralSweep",
                "GoalWorktreeTestsRebaseMerge",
                "GoalWorktreeTestsRemoveCleanup",
                "GoalWorktreeTestsSqliteTooling",
                "LandingExecutorTests",
                "SourceBacklogSiblingCreationTests"
            ]);
        Xunit.Assert.Empty(settings.InfrastructureTestLanes
            .Single(lane => lane.Name == "Goal worktree parallel")
            .ExclusiveResourceKeys);
        var terminalSweepLane = Xunit.Assert.Single(settings.InfrastructureTestLanes
            .Where(lane => LaneIncludesClass(lane, typeof(CliCommandTestsTerminalSweepCommands))));
        Xunit.Assert.Equal("Cli", terminalSweepLane.Name);
        Xunit.Assert.Empty(terminalSweepLane.ExclusiveResourceKeys);
        var gitFactIndexLane = Xunit.Assert.Single(settings.InfrastructureTestLanes
            .Where(lane => LaneIncludesClass(lane, typeof(GoalGitFactIndexTests))));
        Xunit.Assert.Equal("Goal worktree parallel", gitFactIndexLane.Name);
        Xunit.Assert.Empty(gitFactIndexLane.ExclusiveResourceKeys);
        Xunit.Assert.Empty(settings.InfrastructureTestLanes
            .Single(lane => lane.Name == "Goal lifecycle commands")
            .ExclusiveResourceKeys);
        Xunit.Assert.Empty(settings.InfrastructureTestLanes
            .Single(lane => lane.Name == "Goal worktree cleanup")
            .ExclusiveResourceKeys);
        AssertLaneSetPreservesCoverage(
            settings,
            [
                "Worker profiles",
                "Worker dispatch fixtures A",
                "Worker dispatch fixtures B",
                "Worker dispatch fixtures C"
            ],
            [
                "AdvanceLoopTests",
                "ConductLoopLockTests",
                "ConductorLoopHandoffTests",
                "GoalBacklogLinkTests",
                "GoalLifecycleEventWriterTests",
                "RealWorkerProcessGuardTests",
                "SqliteOrchestratorStateRepositoryTests",
                "WorkerDispatchTestsDispatchPreparation",
                "WorkerDispatchTestsModelSelectionEnvMutation",
                "WorkerDispatchTestsSandboxLowIntegrity",
                "WorkerDispatchTestsSubscriptionPreflight",
                "WorkerDispatchTestsWorkerResultClassification",
                "WorkerProcessJobsTests",
                "WorkerProfileTests",
                "WorkspaceConsolidatorTests"
            ]);
        AssertLanePairPreservesCoverage(
            settings,
            "Goal acceptance verifier",
            "Goal acceptance build slots",
            [
                "AcceptanceGateEngineSettingsTests",
                "AcceptanceOutputCaptureTests",
                "AcceptanceOverlappedCheckSchedulingTests",
                "GoalAcceptanceVerifierCancellationTests",
                "GoalAcceptanceVerifierTests",
                "GoalAcceptanceVerifierDotnetBuildSlotTests",
                "HermeticVerificationEnvironmentTests",
                "PreReviewFocusedEvidenceVerifierTests",
                "RealProcessShardAlphaSmokeTests",
                "RealProcessShardBetaSmokeTests",
                "WorkerDispatchJobAccountingTests"
            ]);

        const string providerProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/" +
            "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj";
        const string cliProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/" +
            "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
        const string dashboardProject =
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/" +
            "Mcg.AgentOrchestrator.Dashboard.Tests.csproj";
        _ = settings.ResolveMtpInvocation(providerProject);
        _ = settings.ResolveMtpInvocation(cliProject);
        _ = settings.ResolveMtpInvocation(dashboardProject);
        Xunit.Assert.Equal("Infrastructure.Cli.Tests", GoalAcceptanceVerifier.ProjectLabel(cliProject));

        var manifest = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "config", "acceptance-manifest.json")))!;
        var providerCheck = manifest["checks"]!.AsArray()
            .Select(check => check!.AsObject())
            .Single(check => check["name"]?.GetValue<string>() == "provider environment tests");
        Xunit.Assert.Equal(providerProject, providerCheck["project"]?.GetValue<string>());
        Xunit.Assert.False(providerCheck.ContainsKey("estimatedSerialSeconds"));
        Xunit.Assert.False(providerCheck.ContainsKey("exclusiveResourceKeys"));
        var cliCheck = manifest["checks"]!.AsArray()
            .Select(check => check!.AsObject())
            .Single(check => check["name"]?.GetValue<string>() == "cli tests");
        Xunit.Assert.Equal(cliProject, cliCheck["project"]?.GetValue<string>());
        Xunit.Assert.False(cliCheck.ContainsKey("estimatedSerialSeconds"));
        Xunit.Assert.False(cliCheck.ContainsKey("exclusiveResourceKeys"));
        var dashboardCheck = manifest["checks"]!.AsArray()
            .Select(check => check!.AsObject())
            .Single(check => check["name"]?.GetValue<string>() == "dashboard tests");
        Xunit.Assert.Equal(dashboardProject, dashboardCheck["project"]?.GetValue<string>());
        Xunit.Assert.Equal(
            ["--verbosity", "minimal", "--filter-not-trait", "Category=HostIntegration"],
            dashboardCheck["arguments"]!.AsArray().Select(value => value!.GetValue<string>()));
        Xunit.Assert.False(dashboardCheck.ContainsKey("exclusiveResourceKeys"));

        var solutionText = File.ReadAllText(Path.Combine(repositoryRoot, "Mcg.AgentOrchestrator.sln"));
        var trustedTestProjects = GoalAcceptanceVerifier.DiscoverTrustedTestProjects(repositoryRoot, repositoryRoot);
        Xunit.Assert.Contains(cliProject, trustedTestProjects);
        Xunit.Assert.Contains(dashboardProject, trustedTestProjects);
        Xunit.Assert.All(
            trustedTestProjects,
            project => Xunit.Assert.Contains(
                project.Replace('/', '\\'),
                solutionText,
                StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_partition_verdict_full_rerun_cadence_defaults_and_loads")]
    public void AcceptanceGateEnginePartitionVerdictFullRerunCadenceDefaultsAndLoads()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-engine-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Xunit.Assert.Equal(5, AcceptanceGateEngineSettings.Load(root).PartitionVerdictFullRerunEveryN);

            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(
                Path.Combine(root, "config", "acceptance-manifest.json"),
                """{ "version": 1, "engine": {} }""");
            Xunit.Assert.Equal(5, AcceptanceGateEngineSettings.Load(root).PartitionVerdictFullRerunEveryN);

            File.WriteAllText(
                Path.Combine(root, "config", "acceptance-manifest.json"),
                """{ "version": 1, "engine": { "partitionVerdictFullRerunEveryN": 3 } }""");
            Xunit.Assert.Equal(3, AcceptanceGateEngineSettings.Load(root).PartitionVerdictFullRerunEveryN);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Theory(DisplayName = "AcceptanceGateEngine_rejects_invalid_partition_verdict_full_rerun_cadence")]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void AcceptanceGateEngineRejectsInvalidPartitionVerdictFullRerunCadence(int cadence)
    {
        var root = CreateWorkspace($$"""
            {
              "version": 1,
              "engine": { "partitionVerdictFullRerunEveryN": {{cadence}} }
            }
            """);
        try
        {
            var error = Xunit.Assert.Throws<InvalidDataException>(
                () => AcceptanceGateEngineSettings.Load(root));

            Xunit.Assert.Contains("partitionVerdictFullRerunEveryN must be at least 1", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory(DisplayName = "AcceptanceGateEngine_rejects_invalid_max_concurrent_shards")]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void AcceptanceGateEngineRejectsInvalidMaxConcurrentShards(int maxConcurrentShards)
    {
        var root = CreateWorkspace($$"""
            {
              "version": 1,
              "engine": { "maxConcurrentShards": {{maxConcurrentShards}} }
            }
            """);
        try
        {
            var error = Xunit.Assert.Throws<InvalidDataException>(
                () => AcceptanceGateEngineSettings.Load(root));

            Xunit.Assert.Contains("maxConcurrentShards must be at least 1", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_checked_in_lanes_partition_every_runnable_class")]
    public void AcceptanceGateEngineCheckedInLanesPartitionEveryRunnableClass()
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());
        var runnableClasses = typeof(AcceptanceGateEngineSettingsTests).Assembly
            .GetTypes()
            .Where(IsRunnableTestClass)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        AssertLanePartitionInvariants(settings, runnableClasses);
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_lane_partition_rejects_duplicate_assignment")]
    public void AcceptanceGateEngineLanePartitionRejectsDuplicateAssignment()
    {
        var error = CaptureLanePartitionFailure(
            """
            {
              "version": 1,
              "engine": {
                "infrastructureTestLanes": [
                  { "name": "first", "filter": "FullyQualifiedName~AcceptanceGateEngineSettingsTests" },
                  { "name": "second", "filter": "FullyQualifiedName~AcceptanceGateEngineSettingsTests" }
                ]
              }
            }
            """,
            [typeof(AcceptanceGateEngineSettingsTests)]);

        Xunit.Assert.Contains("assigned to multiple acceptance lanes", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("AcceptanceGateEngineSettingsTests", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("first, second", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_lane_partition_rejects_unassigned_class")]
    public void AcceptanceGateEngineLanePartitionRejectsUnassignedClass()
    {
        var error = CaptureLanePartitionFailure(
            """
            {
              "version": 1,
              "engine": {
                "infrastructureTestLanes": [
                  { "name": "other", "filter": "FullyQualifiedName~GoalAcceptanceVerifierTests" }
                ]
              }
            }
            """,
            [typeof(AcceptanceGateEngineSettingsTests)]);

        Xunit.Assert.Contains("is not assigned to any acceptance lane", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("AcceptanceGateEngineSettingsTests", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_lane_partition_rejects_nonexclusive_class_in_exclusive_lane")]
    public void AcceptanceGateEngineLanePartitionRejectsNonExclusiveClassInExclusiveLane()
    {
        var error = CaptureLanePartitionFailure(
            """
            {
              "version": 1,
              "engine": {
                "infrastructureTestLanes": [{
                  "name": "exclusive candidate",
                  "filter": "FullyQualifiedName~AcceptanceGateEngineSettingsTests|FullyQualifiedName~WorkerDispatchJobAccountingTests",
                  "exclusiveResourceKeys": ["xunit:JobAccounting"]
                }]
              }
            }
            """,
            [typeof(AcceptanceGateEngineSettingsTests), typeof(WorkerDispatchJobAccountingTests)]);

        Xunit.Assert.Contains("exclusive candidate", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("xunit:JobAccounting", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("matched classes do not belong to any declared exclusive xUnit collection", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("AcceptanceGateEngineSettingsTests", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_lane_partition_rejects_empty_lane")]
    public void AcceptanceGateEngineLanePartitionRejectsEmptyLane()
    {
        var error = CaptureLanePartitionFailure(
            """
            {
              "version": 1,
              "engine": {
                "infrastructureTestLanes": [
                  { "name": "assigned", "filter": "FullyQualifiedName~AcceptanceGateEngineSettingsTests" },
                  { "name": "empty", "filter": "FullyQualifiedName~MissingTests" }
                ]
              }
            }
            """,
            [typeof(AcceptanceGateEngineSettingsTests)]);

        Xunit.Assert.Contains("Acceptance lane 'empty'", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("does not match any runnable test class", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource")]
    public void AcceptanceGateEngineDisabledCollectionsSpanningLanesShareAnExclusiveResource()
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());
        var testAssembly = typeof(AcceptanceGateEngineSettingsTests).Assembly;
        var processLocalCollections = testAssembly.GetTypes()
            .Where(type => type.IsDefined(typeof(ProcessLocalTestCollectionAttribute), inherit: false))
            .Select(type =>
            {
                var definition = type.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>();
                Xunit.Assert.NotNull(definition);
                Xunit.Assert.True(definition.DisableParallelization,
                    $"Process-local collection '{type.Name}' must retain in-process serialization.");
                return definition.Name;
            })
            .ToHashSet(StringComparer.Ordinal);
        var disabledCollections = testAssembly
            .GetTypes()
            .Select(type => type.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>())
            .Where(attribute => attribute is { DisableParallelization: true })
            .Select(attribute => attribute!.Name)
            .ToHashSet(StringComparer.Ordinal);
        var mappedTestClasses = testAssembly
            .GetTypes()
            .Where(IsRunnableTestClass)
            .Select(type => (
                Type: type,
                Collection: type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name))
            .Where(entry =>
                entry.Collection is not null &&
                disabledCollections.Contains(entry.Collection))
            .Select(entry =>
            {
                var lanes = settings.InfrastructureTestLanes
                    .Where(lane => LaneIncludesClass(lane, entry.Type))
                    .ToArray();
                Xunit.Assert.True(
                    lanes.Length == 1,
                    $"Disabled-collection test class '{entry.Type.FullName}' mapped to " +
                    $"{lanes.Length} acceptance lanes: [{string.Join(", ", lanes.Select(lane => lane.Name))}].");
                return (
                    Collection: entry.Collection!,
                    ClassName: entry.Type.FullName ?? entry.Type.Name,
                    Lane: lanes[0]);
            })
            .ToArray();

        Xunit.Assert.Equal(
            disabledCollections.Order(StringComparer.Ordinal),
            mappedTestClasses
                .Select(entry => entry.Collection)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        foreach (var collection in mappedTestClasses.GroupBy(
                     entry => entry.Collection,
                     StringComparer.Ordinal))
        {
            var lanes = collection
                .Select(entry => entry.Lane)
                .DistinctBy(lane => lane.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (lanes.Length < 2 || processLocalCollections.Contains(collection.Key))
            {
                continue;
            }

            var sharedKeys = lanes
                .Select(lane => lane.ExclusiveResourceKeys.Select(key => key.Trim()))
                .Aggregate((left, right) => left.Intersect(right, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            Xunit.Assert.True(
                sharedKeys.Length > 0,
                $"Disabled collection '{collection.Key}' spans acceptance lanes " +
                $"[{string.Join(", ", lanes.Select(lane => lane.Name))}] without a shared exclusive resource key. " +
                $"Mapped classes: [{string.Join(", ", collection.Select(entry => $"{entry.ClassName} -> {entry.Lane.Name}"))}].");
        }
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_candidate_lane_and_timeout_change_apply_without_engine_recompile")]
    public async Task AcceptanceGateEngineCandidateLaneAndTimeoutChangeApplyWithoutEngineRecompile()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "timeouts": { "defaultMinutes": 3, "buildServerShutdownMinutes": 1 },
                "infrastructureTestLanes": [
                  {
                     "name": "candidate lane",
                     "filter": "FullyQualifiedName~CandidateLaneTests",
                     "estimatedSerialSeconds": 123.5,
                     "exclusiveResourceKeys": [ "candidate-resource" ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ]
            }
            """);
        var calls = new List<(string[] Arguments, TimeSpan Timeout)>();
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, timeout, _) =>
            {
                calls.Add((arguments, timeout));
                if (arguments.Length >= 2 &&
                    arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                    arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    WriteVstestTrx(arguments, "CandidateLaneTests.Passes");
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            var result = await verifier.RunAsync(root);
            var settings = AcceptanceGateEngineSettings.Load(root);

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.Equal(2, settings.MaxConcurrentShards);
            var lane = Xunit.Assert.Single(settings.InfrastructureTestLanes);
            Xunit.Assert.Equal(123.5, lane.EstimatedSerialSeconds);
            Xunit.Assert.Equal(["candidate-resource"], lane.ExclusiveResourceKeys);
            Xunit.Assert.Equal(TimeSpan.FromMinutes(1), calls[0].Timeout);
            var testCall = Xunit.Assert.Single(calls.Skip(1));
            Xunit.Assert.Contains("FullyQualifiedName~CandidateLaneTests", testCall.Arguments);
            Xunit.Assert.Equal(TimeSpan.FromMinutes(3), testCall.Timeout);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory(DisplayName = "AcceptanceGateEngine_rejects_blank_or_duplicate_exclusive_resource_keys")]
    [Xunit.InlineData("""[ "" ]""", "non-empty")]
    [Xunit.InlineData("""[ "shared", " SHARED " ]""", "duplicated")]
    public void AcceptanceGateEngineRejectsInvalidExclusiveResourceKeys(
        string exclusiveResourceKeys,
        string expectedMessage)
    {
        var root = CreateWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "infrastructureTestLanes": [{
                  "name": "candidate",
                  "filter": "FullyQualifiedName~CandidateTests",
                  "exclusiveResourceKeys": {{exclusiveResourceKeys}}
                }]
              }
            }
            """);
        try
        {
            var error = Xunit.Assert.Throws<InvalidDataException>(
                () => AcceptanceGateEngineSettings.Load(root));

            Xunit.Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_raised_cap_preserves_exclusive_resource_isolation")]
    public async Task GoalAcceptanceVerifierRaisedCapPreservesExclusiveResourceIsolation()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 6,
                "partitionVerdictFullRerunEveryN": 5,
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "candidate/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "candidate/{projectName}.exe",
                  "arguments": [
                    "{executable}",
                    "--results-directory",
                    "{resultsDirectory}",
                    "--report-trx-filename",
                    "{trxFileName}"
                  ]
                }],
                "infrastructureTestLanes": [
                  { "name": "shared alpha", "filter": "FullyQualifiedName~SharedAlphaTests", "exclusiveResourceKeys": ["candidate:shared"] },
                  { "name": "shared beta", "filter": "FullyQualifiedName~SharedBetaTests", "exclusiveResourceKeys": ["CANDIDATE:SHARED"] },
                  { "name": "disjoint gamma", "filter": "FullyQualifiedName~DisjointGammaTests" },
                  { "name": "disjoint delta", "filter": "FullyQualifiedName~DisjointDeltaTests" },
                  { "name": "disjoint epsilon", "filter": "FullyQualifiedName~DisjointEpsilonTests" },
                  { "name": "disjoint zeta", "filter": "FullyQualifiedName~DisjointZetaTests" },
                  { "name": "disjoint eta", "filter": "FullyQualifiedName~DisjointEtaTests" }
                ]
              },
              "checks": [{
                "name": "infrastructure tests",
                "type": "dotnet-test",
                "runner": "mtp",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }]
            }
            """);
        var goalId = new Mcg.AgentOrchestrator.Core.GoalId("12345678123456781234567812345678");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var releaseInitialWave = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeShards = 0;
        var peakShards = 0;
        var activeSharedShards = 0;
        var peakSharedShards = 0;
        DotnetBuildEnvironmentLease? buildLease = null;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 6;
        TestOverrides.ResolveMainWorktreePathForTests = _ => root;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-raised-cap";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-raised-cap";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-raised-cap";
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            buildLease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var stableSlotIndex = buildLease.Environment.BuildPermitIndex
                ?? throw new InvalidOperationException("Expected a scheduler-managed build permit.");
            var verifier = new GoalAcceptanceVerifier(TestOverrides, async (arguments, _, _) =>
            {
                if (!arguments.Contains("--report-trx-filename"))
                {
                    if (arguments.Length >= 2 &&
                        arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                        arguments[1].Equals("build", StringComparison.OrdinalIgnoreCase))
                    {
                        var artifactsPathIndex = Array.IndexOf(arguments, "--artifacts-path");
                        Xunit.Assert.True(
                            artifactsPathIndex >= 0 && artifactsPathIndex + 1 < arguments.Length,
                            "The MTP prebuild must specify its stable-slot artifacts path.");
                        var executable = Path.Combine(
                            arguments[artifactsPathIndex + 1],
                            "candidate",
                            "Mcg.AgentOrchestrator.Infrastructure.Tests.exe");
                        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                        File.WriteAllText(executable, "deterministic raised-cap fixture");
                        File.WriteAllText(
                            Path.Combine(
                                Path.GetDirectoryName(executable)!,
                                "Mcg.AgentOrchestrator.Infrastructure.Tests.dll"),
                            "deterministic raised-cap managed fixture");
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                var laneName = new[]
                    {
                        "SharedAlphaTests",
                        "SharedBetaTests",
                        "DisjointGammaTests",
                        "DisjointDeltaTests",
                        "DisjointEpsilonTests",
                        "DisjointZetaTests",
                        "DisjointEtaTests"
                    }
                    .Single(name => arguments.Any(argument => argument.Contains(name, StringComparison.Ordinal)));
                var isShared = laneName.StartsWith("Shared", StringComparison.Ordinal);
                var currentShards = Interlocked.Increment(ref activeShards);
                ObservePeak(ref peakShards, currentShards);
                if (isShared)
                {
                    var currentSharedShards = Interlocked.Increment(ref activeSharedShards);
                    ObservePeak(ref peakSharedShards, currentSharedShards);
                }

                if (currentShards == 6)
                {
                    releaseInitialWave.TrySetResult(true);
                }

                try
                {
                    await releaseInitialWave.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    WriteMtpTrx(arguments, $"{laneName} passes", $"{laneName}.Runs");
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }
                finally
                {
                    if (isShared)
                    {
                        Interlocked.Decrement(ref activeSharedShards);
                    }

                    Interlocked.Decrement(ref activeShards);
                }
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "raised-cap-attempt"));
            var result = await verifier.RunAsync(
                root,
                goalId,
                stableSlotIndex: stableSlotIndex,
                stableSlotLease: buildLease);

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.Equal(6, peakShards);
            Xunit.Assert.Equal(1, peakSharedShards);
        }
        finally
        {
            buildLease?.Dispose();
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = null;
            TestOverrides.ResolvePartitionVerdictMainShaForTests = null;
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_shard_count_does_not_constrain_build_permit_index")]
    public async Task GoalAcceptanceVerifierShardCountDoesNotConstrainBuildPermitIndex()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1
              },
              "checks": [{
                "name": "core tests",
                "type": "dotnet-test",
                "runner": "vstest",
                "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
              }]
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Length >= 2 &&
                    arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                    arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    WriteVstestTrx(arguments, "CoreTests.Passes");
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 1);

            Xunit.Assert.True(result.Passed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_candidate_MTP_invocation_template_drives_own_gate")]
    public async Task AcceptanceGateEngineCandidateMtpInvocationTemplateDrivesOwnGate()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "mtpInvocations": [
                  {
                    "project": "tests/Example.Tests/Example.Tests.csproj",
                    "executablePathTemplate": "candidate/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "candidate/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--candidate-switch",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "example tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Example.Tests/Example.Tests.csproj"
                }
              ]
            }
            """);
        var calls = new List<string[]>();
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                calls.Add(arguments);
                if (arguments.Contains("--report-trx-filename"))
                {
                    WriteMtpTrx(arguments, "Example.Tests passes", "Example.Tests.Passes");
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            });

            var result = await verifier.RunAsync(root);

            Xunit.Assert.True(result.Passed);
            var mtpCall = Xunit.Assert.Single(calls.Where(call =>
                call.Length > 1 &&
                call[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(call[1]).Equals("Example.Tests", StringComparison.OrdinalIgnoreCase)));
            Xunit.Assert.Contains("--candidate-switch", mtpCall);
            Xunit.Assert.Contains(Path.Combine("candidate", "Example.Tests.dll"), mtpCall[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_rejects_candidate_command_before_MTP_executable")]
    public void AcceptanceGateEngineRejectsCandidateCommandBeforeMtpExecutable()
    {
        var root = CreateWorkspace("""
            {
              "engine": {
                "mtpInvocations": [{
                  "project": "tests/Example.Tests/Example.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}.exe",
                  "arguments": ["candidate-command", "{executable}"]
                }]
              }
            }
            """);
        try
        {
            var error = Xunit.Assert.Throws<InvalidDataException>(
                () => AcceptanceGateEngineSettings.Load(root));

            Xunit.Assert.Contains("first argument", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_requires_executable_extension_token_for_managed_MTP_hosting")]
    public void AcceptanceGateEngineRequiresExecutableExtensionToken()
    {
        var root = CreateWorkspace("""
            {
              "engine": {
                "mtpInvocations": [{
                  "project": "tests/Example.Tests/Example.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}",
                  "arguments": ["{executable}"]
                }]
              }
            }
            """);
        try
        {
            var error = Xunit.Assert.Throws<InvalidDataException>(
                () => AcceptanceGateEngineSettings.Load(root));

            Xunit.Assert.Contains("{executableExtension}", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_does_not_allow_case_alias_to_override_trusted_field")]
    public void AcceptanceGateEngineDoesNotAllowCaseAliasToOverrideTrustedField()
    {
        var root = CreateWorkspace("""
            {
              "engine": {
                "enforceStructuralCoverage": true,
                "EnforceStructuralCoverage": false,
                "mtpInvocations": [{
                  "project": "tests/Example.Tests/Example.Tests.csproj",
                  "executablePathTemplate": "safe/{projectName}{executableExtension}",
                  "ExecutablePathTemplate": "../candidate.exe",
                  "arguments": ["{executable}"]
                }]
              }
            }
            """);
        try
        {
            var settings = AcceptanceGateEngineSettings.Load(root);

            Xunit.Assert.True(settings.EnforceStructuralCoverage);
            var invocation = Xunit.Assert.Single(settings.MtpInvocations);
            Xunit.Assert.Equal(
                "safe/{projectName}{executableExtension}",
                invocation.ExecutablePathTemplate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_structural_coverage_discovers_Core_through_direct_MTP")]
    public async Task GoalAcceptanceVerifierStructuralCoverageDiscoversCoreThroughDirectMtp()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": true,
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": [
                    "{executable}",
                    "--results-directory",
                    "{resultsDirectory}",
                    "--report-trx-filename",
                    "{trxFileName}"
                  ]
                }]
              },
              "checks": [{
                "name": "core tests",
                "type": "dotnet-test",
                "runner": "mtp",
                "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
              }]
            }
            """);
        const string displayName = "structural coverage discovers Core through direct MTP";
        const string relocatedSource =
            "tests/Mcg.AgentOrchestrator.Core.Tests/RelocatedCoverageTests.cs";
        var calls = new List<string[]>();
        var buildPermitChecks = 0;
        var discoveryInvocationCount = 0;
        DotnetBuildEnvironmentLease? buildLease = null;
        TestOverrides.ResolveMainWorktreePathForTests = _ => root;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [relocatedSource];
        try
        {
            buildLease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var buildPermitIndex = buildLease.Environment.BuildPermitIndex
                ?? throw new InvalidOperationException("Expected a scheduler-managed build permit.");
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                calls.Add(arguments);
                if (arguments.Length >= 2 &&
                    arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                    arguments[1].Equals("build", StringComparison.OrdinalIgnoreCase))
                {
                    Xunit.Assert.False(
                        DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(buildPermitIndex),
                        "Every build, including the structural-coverage baseline build, must hold a build permit.");
                    buildPermitChecks++;
                }
                else if (arguments.Contains("--list-tests") ||
                    arguments.Contains("--report-trx-filename"))
                {
                    Xunit.Assert.True(
                        DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(buildPermitIndex),
                        "MTP test execution and discovery must not retain a build permit.");
                }

                if (arguments.Contains("--list-tests"))
                {
                    var listTestsIndex = Array.IndexOf(arguments, "--list-tests");
                    Xunit.Assert.Equal("json", arguments[listTestsIndex + 1]);
                    var discovery = discoveryInvocationCount++ == 0
                        ? CreateMtpDiscoveryJson(
                            root,
                            (displayName, "tests/Mcg.AgentOrchestrator.Core.Tests/CoreCoverageTests.cs"))
                        : CreateMtpDiscoveryJson(
                            root,
                            (displayName, "tests/Mcg.AgentOrchestrator.Core.Tests/CoreCoverageTests.cs"),
                            ("relocated coverage case one", relocatedSource),
                            ("relocated coverage case two", relocatedSource));
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, discovery));
                }

                if (arguments.Contains("--report-trx-filename"))
                {
                    WriteMtpTrx(arguments, displayName, "CoreCoverageTests.Runs");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                stableSlotIndex: buildPermitIndex,
                stableSlotLease: buildLease);

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.Equal(2, buildPermitChecks);
            Xunit.Assert.Contains(result.Checks!, check =>
                check.Name == "structural test coverage" &&
                check.ResultSummary!.Contains("discovered=1", StringComparison.Ordinal));
            var discoveryCalls = calls.Where(call => call.Contains("--list-tests")).ToArray();
            Xunit.Assert.Equal(2, discoveryCalls.Length);
            Xunit.Assert.All(discoveryCalls, call =>
            {
                Xunit.Assert.Equal("dotnet", call[0], StringComparer.OrdinalIgnoreCase);
                Xunit.Assert.EndsWith(".dll", call[1], StringComparison.OrdinalIgnoreCase);
                var listTestsIndex = Array.IndexOf(call, "--list-tests");
                Xunit.Assert.Equal("json", call[listTestsIndex + 1]);
            });
        }
        finally
        {
            buildLease?.Dispose();
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ChangeScopedSelectionDropsExpensiveMtpLaneExceptForBuildSystemChanges()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var previousChangeScoped = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        var previousFullShards = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "1");
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", null);
            var ordinaryChange = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                root,
                ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/WorkspaceConsolidator.cs"]);
            var buildSystemChange = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                root,
                [
                    "Directory.Build.props",
                    "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/WorkspaceConsolidator.cs"
                ]);

            Xunit.Assert.DoesNotContain(
                ordinaryChange,
                check => check.Name.EndsWith(": Mtp managed project rebuild", StringComparison.Ordinal));
            Xunit.Assert.Contains(
                buildSystemChange,
                check => check.Name.EndsWith(": Mtp managed project rebuild", StringComparison.Ordinal));
            Xunit.Assert.Contains(
                ordinaryChange,
                check => check.Name.EndsWith(": Process spawning", StringComparison.Ordinal));
            Xunit.Assert.Contains(
                buildSystemChange,
                check => check.Name.EndsWith(": Process spawning", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previousChangeScoped);
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", previousFullShards);
        }
    }

    [Xunit.Fact]
    public void StructuralCoverageRetainsFilteredOnlyProjectLanesWithoutSynthesizingBroadCheck()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": { "enforceStructuralCoverage": true },
              "checks": [
                {
                  "name": "core tests: alpha",
                  "type": "dotnet-test",
                  "project": "tests/Core.Tests/Core.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~AlphaTests"]
                },
                {
                  "name": "core tests: beta",
                  "type": "dotnet-test",
                  "project": "tests/Core.Tests/Core.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~BetaTests"]
                }
              ]
            }
            """);
        try
        {
            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(root);

            Xunit.Assert.Collection(
                checks,
                check =>
                {
                    Xunit.Assert.Equal("core tests: alpha", check.Name);
                    Xunit.Assert.Equal(["--filter", "FullyQualifiedName~AlphaTests"], check.Arguments);
                },
                check =>
                {
                    Xunit.Assert.Equal("core tests: beta", check.Name);
                    Xunit.Assert.Equal(["--filter", "FullyQualifiedName~BetaTests"], check.Arguments);
                });
            Xunit.Assert.DoesNotContain(checks, check =>
                check.Project == "tests/Core.Tests/Core.Tests.csproj" &&
                !check.Arguments.Contains("--filter", StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void StructuralCoverageLeavesProjectWithUmbrellaCheckUnchanged()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": { "enforceStructuralCoverage": true },
              "checks": [
                {
                  "name": "core tests",
                  "type": "dotnet-test",
                  "project": "tests/Core.Tests/Core.Tests.csproj"
                },
                {
                  "name": "core tests: alpha",
                  "type": "dotnet-test",
                  "project": "tests/Core.Tests/Core.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~AlphaTests"]
                }
              ]
            }
            """);
        try
        {
            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(root);

            Xunit.Assert.Equal(["core tests", "core tests: alpha"], checks.Select(check => check.Name));
            Xunit.Assert.Empty(checks[0].Arguments);
            Xunit.Assert.Equal(
                ["--filter", "FullyQualifiedName~AlphaTests"],
                checks[1].Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void StructuralCoverageDeclarationValidationNeverRemovesEffectiveChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-engine-tests", Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(root, "tests", "Core.Tests", "Core.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, "<Project />");
        var sentinel = new GoalAcceptanceVerifier.AcceptanceManifestCheck { Name = "sentinel", Type = "command" };
        var filtered = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "core tests: alpha",
            Type = "dotnet-test",
            Project = "tests/Core.Tests/Core.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
        };
        var umbrella = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "core tests",
            Type = "dotnet-test",
            Project = "tests/Core.Tests/Core.Tests.csproj"
        };
        try
        {
            var cases = new[]
            {
                (Effective: new[] { sentinel, filtered }, Manifest: new[] { filtered }, Undeclared: 0),
                (Effective: new[] { sentinel, umbrella, filtered }, Manifest: new[] { umbrella, filtered }, Undeclared: 0),
                (Effective: new[] { sentinel }, Manifest: new[] { umbrella }, Undeclared: 0),
                (Effective: new[] { sentinel }, Manifest: Array.Empty<GoalAcceptanceVerifier.AcceptanceManifestCheck>(), Undeclared: 1)
            };

            foreach (var testCase in cases)
            {
                var result = GoalAcceptanceVerifier.EnsureTrustedStructuralCoverageDeclarations(
                    testCase.Effective,
                    testCase.Manifest,
                    root);

                Xunit.Assert.All(testCase.Effective, input =>
                    Xunit.Assert.Contains(result.Checks, output => ReferenceEquals(input, output)));
                Xunit.Assert.Equal(testCase.Undeclared, result.UndeclaredProjects.Count);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task StructuralCoverageReconcilesFilteredOnlyProjectLanes()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": { "maxConcurrentShards": 1, "enforceStructuralCoverage": true },
              "checks": [
                {
                  "name": "core tests: alpha",
                  "type": "dotnet-test",
                  "project": "tests/Core.Tests/Core.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~AlphaTests"]
                },
                {
                  "name": "core tests: beta",
                  "type": "dotnet-test",
                  "project": "tests/Core.Tests/Core.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~BetaTests"]
                }
              ]
            }
            """);
        var executions = new List<string[]>();
        TestOverrides.ResolveMainWorktreePathForTests = _ => root;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Contains("--list-tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "The following Tests are available:\n  AlphaTests.Runs\n  BetaTests.Runs"));
                }

                if (arguments.Length >= 2 && arguments[0] == "dotnet" && arguments[1] == "test")
                {
                    executions.Add(arguments);
                    var testName = arguments.Contains("FullyQualifiedName~AlphaTests")
                        ? "AlphaTests.Runs"
                        : "BetaTests.Runs";
                    WriteVstestTrx(arguments, testName);
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root);

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.Equal(2, executions.Count);
            Xunit.Assert.All(executions, call => Xunit.Assert.Contains("--filter", call));
            Xunit.Assert.Contains(result.Checks!, check =>
                check.Name == "structural test coverage" &&
                check.ResultSummary!.Contains("discovered=2, executed=2", StringComparison.Ordinal));
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task StructuralCoverageCanonicalPartitionsIgnoreFocusedCheckOrdering()
    {
        foreach (var focusedCheckFirst in new[] { true, false })
        {
            var checks = focusedCheckFirst
                ? """
                  {
                    "name": "focused: selected infrastructure tests",
                    "type": "dotnet-test",
                    "runner": "vstest",
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "arguments": ["--filter", "FullyQualifiedName~AlphaTests"]
                  },
                  {
                    "name": "verified: infrastructure suite",
                    "type": "dotnet-test",
                    "runner": "vstest",
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                  }
                  """
                : """
                  {
                    "name": "verified: infrastructure suite",
                    "type": "dotnet-test",
                    "runner": "vstest",
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                  },
                  {
                    "name": "focused: selected infrastructure tests",
                    "type": "dotnet-test",
                    "runner": "vstest",
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "arguments": ["--filter", "FullyQualifiedName~AlphaTests"]
                  }
                  """;
            var root = CreateWorkspace($$"""
                {
                  "version": 1,
                  "engine": {
                    "maxConcurrentShards": 1,
                    "enforceStructuralCoverage": true,
                    "infrastructureTestLanes": [
                      { "name": "alpha", "filter": "FullyQualifiedName~AlphaTests" },
                      { "name": "beta", "filter": "FullyQualifiedName~BetaTests" }
                    ]
                  },
                  "checks": [{{checks}}]
                }
                """);
            TestOverrides.ResolveMainWorktreePathForTests = _ => root;
            TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
            try
            {
                var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
                {
                    if (arguments.Contains("--list-tests"))
                    {
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            0,
                            "The following Tests are available:\n  AlphaTests.Runs\n  BetaTests.Runs"));
                    }

                    if (arguments.Length >= 2 && arguments[0] == "dotnet" && arguments[1] == "test")
                    {
                        WriteVstestTrx(
                            arguments,
                            arguments.Contains("FullyQualifiedName~BetaTests")
                                ? "BetaTests.Runs"
                                : "AlphaTests.Runs");
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                });

                var result = await verifier.RunAsync(root);

                Xunit.Assert.True(result.Passed);
                var structural = Xunit.Assert.Single(result.Checks!, check => check.Name.StartsWith(
                    "structural test coverage", StringComparison.Ordinal));
                Xunit.Assert.True(structural.Passed);
                Xunit.Assert.Contains("discovered=2, executed=2", structural.ResultSummary, StringComparison.Ordinal);
                Xunit.Assert.Contains(result.Checks!, check => check.Name == "verified: infrastructure suite: alpha");
                Xunit.Assert.Contains(result.Checks!, check => check.Name == "verified: infrastructure suite: beta");
            }
            finally
            {
                TestOverrides.ResolveMainWorktreePathForTests = null;
                TestOverrides.ResolveDeletedTestFilesForTests = null;
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public async Task StructuralCoverageCanonicalPartitionsRejectCrossLaneFocusedEvidence()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": true,
                "infrastructureTestLanes": [
                  { "name": "alpha", "filter": "FullyQualifiedName~AlphaTests" },
                  { "name": "beta", "filter": "FullyQualifiedName~BetaTests" }
                ]
              },
              "checks": [
                {
                  "name": "focused: selected infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~AlphaTests"]
                },
                {
                  "name": "verified: infrastructure suite",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ]
            }
            """);
        TestOverrides.ResolveMainWorktreePathForTests = _ => root;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Contains("--list-tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "The following Tests are available:\n  AlphaTests.Runs\n  BetaTests.Runs"));
                }

                if (arguments.Length >= 2 && arguments[0] == "dotnet" && arguments[1] == "test")
                {
                    // A passing receipt in beta that reports alpha must not satisfy beta's required lane.
                    WriteVstestTrx(arguments, "AlphaTests.Runs");
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root);

            Xunit.Assert.False(result.Passed);
            var structural = Xunit.Assert.Single(result.Checks!, check => check.Name.StartsWith(
                "structural test coverage", StringComparison.Ordinal));
            Xunit.Assert.False(structural.Passed);
            Xunit.Assert.Contains("missing=1, emptyPartitions=0", structural.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void StructuralCoverageCanonicalPartitionsRetainMissingRequiredLane()
    {
        var broadCheck = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "verified: infrastructure suite",
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
        };
        var lanes = new[]
        {
            new AcceptanceTestLane("alpha", "FullyQualifiedName~AlphaTests"),
            new AcceptanceTestLane("beta", "FullyQualifiedName~BetaTests")
        };
        var effectiveChecks = new[]
        {
            new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "verified: infrastructure suite: alpha",
                Type = "dotnet-test",
                Project = broadCheck.Project,
                Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
            }
        };

        var partitions = AcceptanceStructuralCoveragePartitionPlan.Resolve(
            broadCheck,
            effectiveChecks,
            lanes);

        Xunit.Assert.Equal(
            [
                "verified: infrastructure suite: alpha",
                "structural coverage missing partition: beta"
            ],
            partitions.Select(partition => partition.Name));
    }

    [Xunit.Fact]
    public void EffectivePlanIdentityBindsScopeAndEnvironmentExpansion()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "checks": [{
                "name": "infrastructure tests",
                "type": "dotnet-test",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }]
            }
            """);
        var previous = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "1");
            var scoped = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                root,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"]);
            var differentScope = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                root,
                ["src/Mcg.AgentOrchestrator.Core/Domain/AcceptanceCohorts.cs"]);
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "0");
            var full = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                root,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"]);

            Xunit.Assert.StartsWith("effective-manifest-sha256-", scoped, StringComparison.Ordinal);
            Xunit.Assert.NotEqual(scoped, differentScope);
            Xunit.Assert.NotEqual(scoped, full);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void EffectivePlanIdentityBindsChangeScopedModeWhenChecksMatch()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "checks": [{
                "name": "git diff whitespace",
                "type": "command",
                "command": "git",
                "arguments": ["diff", "--check"]
              }]
            }
            """);
        var changedFiles = new[] { "Directory.Build.props" };
        var previous = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "1");
            Xunit.Assert.True(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled());
            var scopedChecks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                root,
                changedFiles);
            var scopedIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                root,
                changedFiles);

            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "0");
            Xunit.Assert.False(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled());
            var fullChecks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                root,
                changedFiles);
            var fullIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                root,
                changedFiles);

            Xunit.Assert.NotEmpty(scopedChecks);
            Xunit.Assert.Equal(scopedChecks.Count, fullChecks.Count);
            for (var index = 0; index < scopedChecks.Count; index++)
            {
                var scopedCheck = scopedChecks[index];
                var fullCheck = fullChecks[index];
                Xunit.Assert.Equal(scopedCheck.Name, fullCheck.Name);
                Xunit.Assert.Equal(scopedCheck.Type, fullCheck.Type);
                Xunit.Assert.Equal(scopedCheck.Command, fullCheck.Command);
                Xunit.Assert.Equal(scopedCheck.Project, fullCheck.Project);
                Xunit.Assert.Equal(scopedCheck.Arguments.ToArray(), fullCheck.Arguments.ToArray());
                Xunit.Assert.Equal(scopedCheck.Pattern, fullCheck.Pattern);
                Xunit.Assert.Equal(scopedCheck.FilePath, fullCheck.FilePath);
                Xunit.Assert.Equal(scopedCheck.TimeoutMinutes, fullCheck.TimeoutMinutes);
                Xunit.Assert.Equal(scopedCheck.Advisory, fullCheck.Advisory);
                Xunit.Assert.Equal(scopedCheck.Runner, fullCheck.Runner);
                Xunit.Assert.Equal(scopedCheck.EstimatedSerialSeconds, fullCheck.EstimatedSerialSeconds);
                Xunit.Assert.Equal(
                    scopedCheck.ExclusiveResourceKeys.ToArray(),
                    fullCheck.ExclusiveResourceKeys.ToArray());
            }

            Xunit.Assert.NotEqual(scopedIdentity, fullIdentity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task GoalAcceptanceVerifierManifestChangeInvalidatesAllPartitionVerdicts()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": true,
                "partitionVerdictFullRerunEveryN": 5,
                "infrastructureTestLanes": [
                  { "name": "alpha", "filter": "FullyQualifiedName~AlphaTests" },
                  { "name": "beta", "filter": "FullyQualifiedName~BetaTests" }
                ]
              },
              "checks": [{
                "name": "infrastructure tests",
                "type": "dotnet-test",
                "runner": "vstest",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }]
            }
            """);
        var goalId = new Mcg.AgentOrchestrator.Core.GoalId("12345678123456781234567812345678");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var partitionExecutions = 0;
        var partitionArguments = new List<string[]>();
        TestOverrides.ResolveMainWorktreePathForTests = _ => root;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-mixed-trx";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-mixed-trx";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-mixed-trx";
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Contains("--list-tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "The following Tests are available:\n  AlphaTests.Runs\n  BetaTests.Runs"));
                }

                if (arguments.Length >= 2 &&
                    arguments[0] == "dotnet" &&
                    arguments[1] == "test")
                {
                    partitionExecutions++;
                    partitionArguments.Add(arguments);
                    var testName = arguments.Any(argument =>
                        argument.Contains("AlphaTests", StringComparison.Ordinal))
                            ? "AlphaTests.Runs"
                            : "BetaTests.Runs";
                    WriteVstestTrx(arguments, testName);
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-one"));
            var first = await verifier.RunAsync(root, goalId);
            Xunit.Assert.True(first.Passed);
            Xunit.Assert.Equal(2, partitionExecutions);
            var firstRunPartitionCount = partitionArguments.Count;

            var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["engine"]!["infrastructureTestLanes"]![1]!["filter"] =
                "FullyQualifiedName~BetaTestsChanged";
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-two"));
            var second = await verifier.RunAsync(root, goalId);

            Xunit.Assert.True(second.Passed);
            Xunit.Assert.Equal(4, partitionExecutions);
            var secondRunPartitionArguments = partitionArguments.Skip(firstRunPartitionCount).ToArray();
            Xunit.Assert.Equal(2, secondRunPartitionArguments.Length);
            Xunit.Assert.Contains(secondRunPartitionArguments, arguments =>
                arguments.Contains("FullyQualifiedName~BetaTestsChanged"));
            Xunit.Assert.DoesNotContain(secondRunPartitionArguments, arguments =>
                arguments.Contains("FullyQualifiedName~BetaTests"));
            var cacheReceipt = Xunit.Assert.Single(
                second.Checks!,
                check => check.Name == "infrastructure partition verdict cache");
            Xunit.Assert.DoesNotContain("source_attempt_id=attempt-one", cacheReceipt.ResultSummary);
            Xunit.Assert.Contains("{partition_id=alpha,verdict=GREEN}", cacheReceipt.ResultSummary);
            Xunit.Assert.Contains("{partition_id=beta,verdict=GREEN}", cacheReceipt.ResultSummary);
            Xunit.Assert.Contains(second.Checks!, check =>
                check.Name.StartsWith("structural test coverage", StringComparison.Ordinal) &&
                check.Passed &&
                check.ResultSummary!.Contains("discovered=2, executed=2", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = null;
            TestOverrides.ResolvePartitionVerdictMainShaForTests = null;
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_structural_coverage_rejects_test_project_omitted_from_manifest")]
    public async Task GoalAcceptanceVerifierStructuralCoverageRejectsTestProjectOmittedFromManifest()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": true
              },
              "checks": [{
                "name": "core tests",
                "type": "dotnet-test",
                "runner": "vstest",
                "project": "tests/Core.Tests/Core.Tests.csproj"
              }]
            }
            """);
        var omittedProject = Path.Combine(root, "tests", "Added.Tests", "Added.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(omittedProject)!);
        File.WriteAllText(
            omittedProject,
            "<Project><PropertyGroup><IsTestProject>true</IsTestProject><UseMicrosoftTestingPlatformRunner>false</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
        var calls = new List<string[]>();
        TestOverrides.ResolveMainWorktreePathForTests = _ => root;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                calls.Add(arguments);
                var addedProject = arguments.Any(argument =>
                    argument.EndsWith("Added.Tests.csproj", StringComparison.OrdinalIgnoreCase));
                var testName = addedProject ? "AddedTests.Runs" : "CoreTests.Runs";
                if (arguments.Contains("--list-tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        $"The following Tests are available:\n  {testName}"));
                }

                if (arguments.Length >= 2 &&
                    arguments[0] == "dotnet" &&
                    arguments[1] == "test")
                {
                    WriteVstestTrx(arguments, testName);
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root);

            Xunit.Assert.False(result.Passed);
            var declaration = Xunit.Assert.Single(
                result.Checks!,
                check => check.Name == "structural coverage declaration");
            Xunit.Assert.Contains(
                "tests/Added.Tests/Added.Tests.csproj",
                declaration.OutputTail,
                StringComparison.Ordinal);
            Xunit.Assert.Contains("declared by a dotnet-test check", declaration.OutputTail, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call.Any(argument =>
                    argument.EndsWith("Added.Tests.csproj", StringComparison.OrdinalIgnoreCase)) &&
                !call.Contains("--list-tests"));
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_structural_coverage_fails_when_manifest_lane_filters_out_discovered_test")]
    public async Task GoalAcceptanceVerifierStructuralCoverageFailsWhenManifestLaneFiltersOutDiscoveredTest()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": true,
                "infrastructureTestLanes": [
                  { "name": "narrow", "filter": "FullyQualifiedName~IncludedTests" }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--settings", "candidate-narrowing.runsettings"]
                }
              ]
            }
            """);
        var calls = new List<string[]>();
        TestOverrides.ResolveMainWorktreePathForTests = _ => root;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                calls.Add(arguments);
                if (arguments.Contains("--list-tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "The following Tests are available:\n  IncludedTests.Runs\n  DroppedTests.WasFilteredOut"));
                }

                if (arguments.Length >= 2 &&
                    arguments[0] == "dotnet" &&
                    arguments[1] == "test")
                {
                    WriteVstestTrx(arguments, "IncludedTests.Runs");
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            var result = await verifier.RunAsync(root);

            Xunit.Assert.False(result.Passed);
            var coverage = Xunit.Assert.Single(result.Checks!, check =>
                check.Name == "structural test coverage: infrastructure tests");
            Xunit.Assert.Contains("DroppedTests.WasFilteredOut", coverage.OutputTail, StringComparison.Ordinal);
            Xunit.Assert.All(
                calls.Where(call => call.Contains("--list-tests")),
                call => Xunit.Assert.DoesNotContain("candidate-narrowing.runsettings", call));
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_semantic_duplicate_partitions_execute_once")]
    public async Task GoalAcceptanceVerifierSemanticDuplicatePartitionsExecuteOnce()
    {
        const string checkName = "infrastructure tests: Remainder";
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "partitionVerdictFullRerunEveryN": 1
              },
              "checks": [
                {
                  "name": "infrastructure tests: Remainder",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~RemainderTests"]
                },
                {
                  "name": "infrastructure tests: Remainder",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~RemainderTests"]
                }
              ]
            }
            """);
        var goalId = new Mcg.AgentOrchestrator.Core.GoalId("11111111111111111111111111111111");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var attemptPrefix = Path.Combine(root, ".orchestrator", "semantic-dedup-attempt");
        var invocations = 0;
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-semantic-dedup";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-semantic-dedup";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-semantic-dedup";
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
        try
        {
            var planned = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(root);
            Xunit.Assert.Single(planned, check => check.Name == checkName);

            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Length < 2 ||
                    !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                    !arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build server shutdown succeeded."));
                }

                invocations++;
                WriteVstestTrx(arguments, "RemainderTests.Passes", testCount: 110);
                var heartbeatPath =
                    $"{attemptPrefix}.infrastructure-tests-remainder-{GoalAcceptanceVerifier.ShortHash(checkName)}.{GateHeartbeatArtifacts.FileName}";
                File.WriteAllText(heartbeatPath, "{}");
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 110"));
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);
            var result = await verifier.RunAsync(root, goalId);

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.False(result.Retried);
            Xunit.Assert.Equal(1, invocations);
            var partition = Xunit.Assert.Single(result.Checks!, check => check.Name == checkName);
            Xunit.Assert.Equal(110, partition.DiscoveredTestCount);
            Xunit.Assert.Equal(110, partition.ExecutedTestCount);
            Xunit.Assert.Equal("Completed", partition.CompletionDecision!.TrxOutcome);
            Xunit.Assert.Equal(0, partition.TestResultRunOrdinal);
            var trxPath = Xunit.Assert.Single(partition.TestResultPaths!);
            Xunit.Assert.True(File.Exists(trxPath));
            Xunit.Assert.Equal("Completed", ReadTrxSummaryOutcome(trxPath));
            Xunit.Assert.DoesNotContain("-run-1", Path.GetFileName(trxPath), StringComparison.Ordinal);

            var attemptDirectory = Path.GetDirectoryName(attemptPrefix)!;
            var attemptFilePrefix = Path.GetFileName(attemptPrefix);
            var heartbeatPath = Xunit.Assert.Single(Directory.GetFiles(
                attemptDirectory,
                $"{attemptFilePrefix}.infrastructure-tests-remainder*.gate-heartbeat.json"));
            Xunit.Assert.DoesNotContain("-run-1", Path.GetFileName(heartbeatPath), StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain(
                GoalOperationJournal.Read(root, goalId).Entries,
                entry => entry.Operation == "acceptance:partition-within-attempt-retry");
            var deduplicationReceipt = Xunit.Assert.Single(
                result.Checks!,
                check => check.Name == "semantic execution deduplication: remainder");
            Xunit.Assert.True(deduplicationReceipt.Advisory);
            Xunit.Assert.Contains("semantic_key_sha256=", deduplicationReceipt.ResultSummary, StringComparison.Ordinal);
            Xunit.Assert.Contains(
                GoalOperationJournal.Read(root, goalId).Entries,
                entry => entry.Operation == "acceptance:semantic-check-deduplication" &&
                    entry.PartitionId == "remainder");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = null;
            TestOverrides.ResolvePartitionVerdictMainShaForTests = null;
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_semantic_execution_key_covers_every_execution_control")]
    public void GoalAcceptanceVerifierSemanticExecutionKeyCoversEveryExecutionControl()
    {
        var token = new GoalAcceptanceVerifier.FocusedEvidenceFilterToken(
            "FullyQualifiedName~Example",
            "FullyQualifiedName~Example",
            GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Class,
            "Example",
            "Example");
        GoalAcceptanceVerifier.AcceptanceManifestCheck Create(
            int? timeoutMinutes = 1,
            bool advisory = false,
            double estimatedSerialSeconds = 2,
            IReadOnlyList<string>? exclusiveResourceKeys = null,
            bool focused = false,
            IReadOnlyList<IReadOnlyList<GoalAcceptanceVerifier.FocusedEvidenceFilterToken>>? selections = null) =>
            new()
            {
                Name = "infrastructure tests: Remainder",
                Type = "dotnet-test",
                Runner = "vstest",
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", "FullyQualifiedName~RemainderTests"],
                TimeoutMinutes = timeoutMinutes,
                Advisory = advisory,
                EstimatedSerialSeconds = estimatedSerialSeconds,
                ExclusiveResourceKeys = exclusiveResourceKeys ?? ["dotnet-build"],
                IsFocusedEvidenceSelection = focused,
                FocusedEvidenceTokens = focused ? [token] : [],
                FocusedEvidenceSelections = selections ?? []
            };

        var baseline = GoalAcceptanceVerifier.SemanticExecutionKeyForTests(Create());
        Xunit.Assert.NotEqual(baseline, GoalAcceptanceVerifier.SemanticExecutionKeyForTests(Create(timeoutMinutes: 2)));
        Xunit.Assert.NotEqual(baseline, GoalAcceptanceVerifier.SemanticExecutionKeyForTests(Create(advisory: true)));
        Xunit.Assert.NotEqual(baseline, GoalAcceptanceVerifier.SemanticExecutionKeyForTests(Create(estimatedSerialSeconds: 3)));
        Xunit.Assert.NotEqual(baseline, GoalAcceptanceVerifier.SemanticExecutionKeyForTests(Create(exclusiveResourceKeys: ["another-resource"])));
        Xunit.Assert.NotEqual(
            baseline,
            GoalAcceptanceVerifier.SemanticExecutionKeyForTests(Create(focused: true, selections: [[token]])));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_same_named_checks_retain_invocation_receipts")]
    public async Task GoalAcceptanceVerifierSameNamedChecksRetainInvocationReceipts()
    {
        const string checkName = "infrastructure tests: Remainder";
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "partitionVerdictFullRerunEveryN": 1
              },
              "checks": [
                {
                  "name": "infrastructure tests: Remainder",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~FirstRemainderTests"]
                },
                {
                  "name": "infrastructure tests: Remainder",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~SecondRemainderTests"]
                }
              ]
            }
            """);
        var goalId = new Mcg.AgentOrchestrator.Core.GoalId("12345678123456781234567812345678");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var attemptPrefix = Path.Combine(root, ".orchestrator", "same-name-attempt");
        var invocation = 0;
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-same-name";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-same-name";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-same-name";
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var planned = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(root);
            Xunit.Assert.Equal(2, planned.Count(check => check.Name == checkName));

            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Length < 2 ||
                    !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                    !arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build server shutdown succeeded."));
                }

                var currentInvocation = invocation++;
                var passed = currentInvocation == 1;
                WriteVstestTrx(
                    arguments,
                    currentInvocation == 0 ? "FirstRemainderTests.Fails" : "SecondRemainderTests.Passes",
                    passed ? "Passed" : "Failed");
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    passed ? 0 : 1,
                    passed ? "Passed: 1" : "Failed: 1"));
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);
            var result = await verifier.RunAsync(root, goalId);

            var duplicateResults = result.Checks!
                .Where(check => check.Name == checkName)
                .ToArray();
            Xunit.Assert.Equal(2, duplicateResults.Length);
            Xunit.Assert.False(result.Passed);
            Xunit.Assert.False(duplicateResults[0].Passed);
            Xunit.Assert.True(duplicateResults[1].Passed);
            var failedPath = Xunit.Assert.Single(duplicateResults[0].TestResultPaths!);
            var passedPath = Xunit.Assert.Single(duplicateResults[1].TestResultPaths!);
            Xunit.Assert.NotEqual(failedPath, passedPath);
            Xunit.Assert.True(File.Exists(failedPath), $"Failed invocation receipt was not retained: {failedPath}");
            Xunit.Assert.True(File.Exists(passedPath), $"Passing invocation receipt was not retained: {passedPath}");
            Xunit.Assert.Equal("Failed", ReadTrxOutcome(failedPath));
            Xunit.Assert.Equal("Passed", ReadTrxOutcome(passedPath));
            Xunit.Assert.Equal(0, duplicateResults[0].TestResultRunOrdinal);
            Xunit.Assert.Equal(1, duplicateResults[1].TestResultRunOrdinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = null;
            TestOverrides.ResolvePartitionVerdictMainShaForTests = null;
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_within_attempt_rerun_reaps_previous_invocation_heartbeat_child")]
    public async Task GoalAcceptanceVerifierWithinAttemptRerunReapsPreviousInvocationHeartbeatChild()
    {
        const string checkName = "infrastructure tests: Remainder";
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "partitionVerdictFullRerunEveryN": 1
              },
              "checks": [
                {
                  "name": "infrastructure tests: Remainder",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~RemainderTests"]
                }
              ]
            }
            """);
        var goalId = new Mcg.AgentOrchestrator.Core.GoalId("87654321876543218765432187654321");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var attemptPrefix = Path.Combine(root, ".orchestrator", "heartbeat-rerun-attempt");
        var invocation = 0;
        var sleeperStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        sleeperStartInfo.ArgumentList.Add("-NoProfile");
        sleeperStartInfo.ArgumentList.Add("-Command");
        sleeperStartInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        using var sleeper = System.Diagnostics.Process.Start(sleeperStartInfo)
            ?? throw new InvalidOperationException("Failed to start heartbeat child process.");
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-heartbeat-rerun";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-heartbeat-rerun";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-heartbeat-rerun";
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Length < 2 ||
                    !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                    !arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build server shutdown succeeded."));
                }

                var currentInvocation = invocation++;
                if (currentInvocation == 0)
                {
                    var environment = DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId);
                    var heartbeatPath = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                        checkName,
                        environment,
                        attemptResultsPrefix: attemptPrefix);
                    GateHeartbeatArtifacts.Write(
                        heartbeatPath,
                        new GateHeartbeatSnapshot(
                            goalId.Value,
                            "verification-check",
                            checkName,
                            null,
                            sleeper.Id,
                            sleeper.Id,
                            "completed",
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            0,
                            0,
                            0,
                            $"dotnet test --artifacts-path {environment.ArtifactsPath}"));
                }
                else
                {
                    Xunit.Assert.True(
                        sleeper.HasExited,
                        "Previous invocation heartbeat child remained alive before the later same-named check ran.");
                }

                if (currentInvocation != 0)
                {
                    WriteVstestTrx(
                        arguments,
                        "RemainderTests.Passes",
                        "Passed");
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    currentInvocation == 0 ? 1 : 0,
                    currentInvocation == 0 ? "Test runner exited before producing TRX." : "Passed: 1"));
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);
            var result = await verifier.RunAsync(root, goalId);

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.True(result.Retried);
            Xunit.Assert.Equal(2, invocation);
        }
        finally
        {
            if (!sleeper.HasExited)
            {
                sleeper.Kill(entireProcessTree: true);
                sleeper.WaitForExit();
            }
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = null;
            TestOverrides.ResolvePartitionVerdictMainShaForTests = null;
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_missing_retry_heartbeat_preserves_original_red_result")]
    public async Task GoalAcceptanceVerifierMissingRetryHeartbeatPreservesOriginalRedResult()
    {
        const string checkName = "infrastructure tests: Remainder";
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": { "maxConcurrentShards": 1, "partitionVerdictFullRerunEveryN": 1 },
              "checks": [
                {
                  "name": "infrastructure tests: Remainder",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~RemainderTests"]
                }
              ]
            }
            """);
        var goalId = new Mcg.AgentOrchestrator.Core.GoalId("99999999999999999999999999999999");
        var invocations = 0;
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-missing-heartbeat";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-missing-heartbeat";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-missing-heartbeat";
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Length < 2 || arguments[0] != "dotnet" || arguments[1] != "test")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build server shutdown succeeded."));
                }

                invocations++;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    1,
                    "Failed: 1",
                    Stderr: "original retry-driving stderr"));
            });

            var result = await verifier.RunAsync(root, goalId);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.False(result.Retried);
            Xunit.Assert.Equal(1, invocations);
            var partition = Xunit.Assert.Single(result.Checks!, check => check.Name == checkName);
            Xunit.Assert.Equal(AcceptanceFailureClassifications.RetryEvidenceRetentionFailed, partition.FailureClassification);
            Xunit.Assert.Contains("retry refused", partition.OutputTail, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.NotNull(partition.ProcessStderrPath);
            Xunit.Assert.True(File.Exists(partition.ProcessStderrPath));
            Xunit.Assert.DoesNotContain(
                GoalOperationJournal.Read(root, goalId).Entries,
                entry => entry.Operation == "acceptance:partition-within-attempt-retry");
        }
        finally
        {
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = null;
            TestOverrides.ResolvePartitionVerdictMainShaForTests = null;
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_distinct_named_checks_keep_legacy_telemetry_names")]
    public async Task GoalAcceptanceVerifierDistinctNamedChecksKeepLegacyTelemetryNames()
    {
        const string alphaName = "infrastructure tests: Alpha";
        const string betaName = "infrastructure tests: Beta";
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "partitionVerdictFullRerunEveryN": 1
              },
              "checks": [
                {
                  "name": "infrastructure tests: Alpha",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~AlphaTests"]
                },
                {
                  "name": "infrastructure tests: Beta",
                  "type": "dotnet-test",
                  "runner": "vstest",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--filter", "FullyQualifiedName~BetaTests"]
                }
              ]
            }
            """);
        var goalId = new Mcg.AgentOrchestrator.Core.GoalId("12345678123456781234567812345678");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var attemptPrefix = Path.Combine(root, ".orchestrator", "distinct-name-attempt");
        TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = _ => "tree-distinct-name";
        TestOverrides.ResolvePartitionVerdictMainShaForTests = _ => "main-distinct-name";
        TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = _ => "commit-distinct-name";
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (arguments, _, _) =>
            {
                if (arguments.Length < 2 ||
                    !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                    !arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build server shutdown succeeded."));
                }

                var testName = arguments.Any(argument =>
                    argument.Contains("AlphaTests", StringComparison.Ordinal))
                        ? "AlphaTests.Passes"
                        : "BetaTests.Passes";
                WriteVstestTrx(arguments, testName);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);
            var result = await verifier.RunAsync(root, goalId);

            Xunit.Assert.True(result.Passed);
            var alpha = Xunit.Assert.Single(result.Checks!, check => check.Name == alphaName);
            var beta = Xunit.Assert.Single(result.Checks!, check => check.Name == betaName);
            Xunit.Assert.Equal(
                "distinct-name-attempt.infrastructure-tests-alpha.trx",
                Path.GetFileName(Xunit.Assert.Single(alpha.TestResultPaths!)));
            Xunit.Assert.Equal(
                "distinct-name-attempt.infrastructure-tests-beta.trx",
                Path.GetFileName(Xunit.Assert.Single(beta.TestResultPaths!)));
            var heartbeatEnvironment = new DotnetBuildEnvironment(
                "distinct-name-heartbeat",
                root,
                Path.Combine(root, "build-artifacts"),
                Path.Combine(root, "build-slots", "build-0.lock"),
                [],
                "distinct-name-heartbeat");
            Xunit.Assert.Equal(
                $"distinct-name-attempt.infrastructure-tests-alpha-{GoalAcceptanceVerifier.ShortHash(alphaName)}.{GateHeartbeatArtifacts.FileName}",
                Path.GetFileName(GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                    alphaName,
                    heartbeatEnvironment,
                    attemptResultsPrefix: attemptPrefix)));
            Xunit.Assert.Equal(
                $"distinct-name-attempt.infrastructure-tests-beta-{GoalAcceptanceVerifier.ShortHash(betaName)}.{GateHeartbeatArtifacts.FileName}",
                Path.GetFileName(GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
                    betaName,
                    heartbeatEnvironment,
                    attemptResultsPrefix: attemptPrefix)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            TestOverrides.ResolvePartitionVerdictCandidateTreeShaForTests = null;
            TestOverrides.ResolvePartitionVerdictMainShaForTests = null;
            TestOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), manifest);
        using var document = System.Text.Json.JsonDocument.Parse(manifest);
        if (document.RootElement.TryGetProperty("checks", out var checks))
        {
            foreach (var check in checks.EnumerateArray())
            {
                if (!check.TryGetProperty("project", out var projectElement))
                {
                    continue;
                }

                var project = projectElement.GetString();
                if (string.IsNullOrWhiteSpace(project) ||
                    !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var projectPath = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
                File.WriteAllText(projectPath, "<Project />");
            }
        }

        return root;
    }

    private static void ObservePeak(ref int peak, int candidate)
    {
        var observed = Volatile.Read(ref peak);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref peak, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private static void WriteVstestTrx(
        string[] arguments,
        string testName,
        string outcome = "Passed",
        int testCount = 1,
        int skippedCount = 0,
        string summaryOutcome = "Completed")
    {
        var resultsDirectoryIndex = Array.IndexOf(arguments, "--results-directory");
        var loggerIndex = Array.IndexOf(arguments, "--logger");
        Xunit.Assert.True(resultsDirectoryIndex >= 0 && loggerIndex >= 0);
        var resultsDirectory = arguments[resultsDirectoryIndex + 1];
        var logger = arguments[loggerIndex + 1];
        const string prefix = "trx;LogFileName=";
        Xunit.Assert.StartsWith(prefix, logger, StringComparison.Ordinal);
        Directory.CreateDirectory(resultsDirectory);
        var testClass = testName[..testName.LastIndexOf('.')];
        var method = testName[(testName.LastIndexOf('.') + 1)..];
        string ReceiptTestName(int index) => testCount == 1 ? testName : $"{testName}.{index}";
        string ReceiptMethodName(int index) => testCount == 1 ? method : $"{method}{index}";
        var definitions = string.Concat(Enumerable.Range(1, testCount).Select(index =>
            $"<UnitTest id=\"{index}\" name=\"{ReceiptTestName(index)}\"><TestMethod className=\"{testClass}\" name=\"{ReceiptMethodName(index)}\" /></UnitTest>"));
        var executedCount = testCount - skippedCount;
        var results = string.Concat(Enumerable.Range(1, testCount).Select(index =>
            $"<UnitTestResult testId=\"{index}\" testName=\"{ReceiptTestName(index)}\" outcome=\"{(index <= executedCount ? outcome : "NotExecuted")}\" />"));
        var passed = outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) ? executedCount : 0;
        var failed = outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) ? 0 : executedCount;
        File.WriteAllText(
            Path.Combine(resultsDirectory, logger[prefix.Length..]),
            $"<TestRun><TestDefinitions>{definitions}</TestDefinitions><Results>{results}</Results><ResultSummary outcome=\"{summaryOutcome}\"><Counters total=\"{testCount}\" executed=\"{executedCount}\" passed=\"{passed}\" failed=\"{failed}\" notExecuted=\"{skippedCount}\" /></ResultSummary></TestRun>");
    }

    private static string ReadTrxOutcome(string path) =>
        System.Xml.Linq.XDocument.Load(path)
            .Descendants()
            .Single(element => element.Name.LocalName == "UnitTestResult")
            .Attribute("outcome")!
            .Value;

    private static string ReadTrxSummaryOutcome(string path) =>
        System.Xml.Linq.XDocument.Load(path)
            .Descendants()
            .Single(element => element.Name.LocalName == "ResultSummary")
            .Attribute("outcome")!
            .Value;

    private static void WriteMtpTrx(string[] arguments, string displayName, string methodIdentity)
    {
        var resultsDirectoryIndex = Array.IndexOf(arguments, "--results-directory");
        var trxFileIndex = Array.IndexOf(arguments, "--report-trx-filename");
        Xunit.Assert.True(resultsDirectoryIndex >= 0 && trxFileIndex >= 0);
        var resultsDirectory = arguments[resultsDirectoryIndex + 1];
        Directory.CreateDirectory(resultsDirectory);
        var testClass = methodIdentity[..methodIdentity.LastIndexOf('.')];
        var method = methodIdentity[(methodIdentity.LastIndexOf('.') + 1)..];
        File.WriteAllText(
            Path.Combine(resultsDirectory, arguments[trxFileIndex + 1]),
            $"<TestRun><TestDefinitions><UnitTest id=\"1\" name=\"{displayName}\"><TestMethod className=\"{testClass}\" name=\"{method}\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"1\" testName=\"{displayName}\" outcome=\"Passed\" /></Results><ResultSummary outcome=\"Completed\"><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");
    }

    private static string CreateMtpDiscoveryJson(
        string repositoryRoot,
        params (string DisplayName, string SourceFile)[] tests) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            tests = tests.Select((test, index) => new
            {
                uid = $"test-{index:D4}",
                displayName = test.DisplayName,
                location = new
                {
                    file = Path.Combine(
                        repositoryRoot,
                        test.SourceFile.Replace('/', Path.DirectorySeparatorChar)),
                    lineStart = index + 1,
                    lineEnd = index + 1
                }
            })
        });

    private static InvalidDataException CaptureLanePartitionFailure(
        string manifest,
        IReadOnlyList<Type> runnableClasses)
    {
        var root = CreateWorkspace(manifest);
        try
        {
            var settings = AcceptanceGateEngineSettings.Load(root);
            return Xunit.Assert.Throws<InvalidDataException>(
                () => AssertLanePartitionInvariants(
                    settings,
                    runnableClasses,
                    requireExclusiveCollectionMembership: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertLanePartitionInvariants(
        AcceptanceGateEngineSettings settings,
        IReadOnlyList<Type> runnableClasses,
        bool requireExclusiveCollectionMembership = false)
    {
        if (runnableClasses.Count == 0)
        {
            throw new InvalidDataException("Lane partition validation received no runnable test classes.");
        }

        var classesByLane = settings.InfrastructureTestLanes.ToDictionary(
            lane => lane.Name,
            _ => new List<Type>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var type in runnableClasses.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var matchingLanes = settings.InfrastructureTestLanes
                .Where(lane => LaneIncludesClass(lane, type))
                .ToArray();
            var className = type.FullName ?? type.Name;
            if (matchingLanes.Length == 0)
            {
                throw new InvalidDataException(
                    $"Runnable test class '{className}' is not assigned to any acceptance lane.");
            }

            if (matchingLanes.Length > 1)
            {
                throw new InvalidDataException(
                    $"Runnable test class '{className}' is assigned to multiple acceptance lanes: " +
                    $"[{string.Join(", ", matchingLanes.Select(lane => lane.Name))}].");
            }

            classesByLane[matchingLanes[0].Name].Add(type);
        }

        foreach (var lane in settings.InfrastructureTestLanes)
        {
            var matchedClasses = classesByLane[lane.Name];
            if (matchedClasses.Count == 0)
            {
                throw new InvalidDataException(
                    $"Acceptance lane '{lane.Name}' does not match any runnable test class.");
            }

            var declaredXunitCollections = lane.ExclusiveResourceKeys
                .Select(resourceKey => resourceKey.Trim())
                .Where(resourceKey => resourceKey.StartsWith("xunit:", StringComparison.OrdinalIgnoreCase))
                .Select(resourceKey => resourceKey["xunit:".Length..])
                .ToArray();
            var matchedXunitCollections = matchedClasses
                .Select(type => type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name)
                .Where(collectionName => collectionName is not null)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var resourceKey in lane.ExclusiveResourceKeys)
            {
                const string xunitPrefix = "xunit:";
                var normalizedKey = resourceKey.Trim();
                if (!normalizedKey.StartsWith(xunitPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var collectionName = normalizedKey[xunitPrefix.Length..];
                if (matchedXunitCollections.Contains(collectionName) &&
                    (!requireExclusiveCollectionMembership || matchedClasses.All(type =>
                        declaredXunitCollections.Contains(
                            type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name,
                            StringComparer.Ordinal))))
                {
                    continue;
                }

                var nonExclusiveClasses = matchedClasses
                    .Where(type => !declaredXunitCollections.Contains(
                        type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name,
                        StringComparer.Ordinal))
                    .Select(type => type.FullName ?? type.Name);
                throw new InvalidDataException(
                    $"Acceptance lane '{lane.Name}' declares exclusive resource key '{normalizedKey}', " +
                    $"but matched classes do not belong to any declared exclusive xUnit collection: " +
                    $"[{string.Join(", ", nonExclusiveClasses)}].");
            }
        }
    }

    private static void AssertLanePairPreservesCoverage(
        AcceptanceGateEngineSettings settings,
        string firstLaneName,
        string secondLaneName,
        IReadOnlyList<string> expectedClasses)
        => AssertLaneSetPreservesCoverage(settings, [firstLaneName, secondLaneName], expectedClasses);

    private static void AssertLaneSetPreservesCoverage(
        AcceptanceGateEngineSettings settings,
        IReadOnlyList<string> laneNames,
        IReadOnlyList<string> expectedClasses)
    {
        var laneClasses = laneNames
            .Select(name => FilterClasses(settings.InfrastructureTestLanes.Single(lane => lane.Name == name).Filter))
            .ToArray();

        Xunit.Assert.Equal(
            laneClasses.Sum(classes => classes.Length),
            laneClasses.SelectMany(classes => classes).Distinct(StringComparer.Ordinal).Count());
        Xunit.Assert.Equal(
            expectedClasses.Order(StringComparer.Ordinal),
            laneClasses.SelectMany(classes => classes).Order(StringComparer.Ordinal));
    }

    private static bool IsRunnableTestClass(Type type) =>
        type is { IsClass: true, IsAbstract: false } &&
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Any(method => method.GetCustomAttributes(inherit: true).Any(attribute => attribute is Xunit.FactAttribute));

    private static bool LaneIncludesClass(AcceptanceTestLane lane, Type type)
    {
        var className = type.FullName ?? type.Name;
        var translated = GoalAcceptanceVerifier.TranslateMtpFilter(lane.Filter).ToArray();
        var included = new List<string>();
        var excluded = new List<string>();
        for (var index = 0; index < translated.Length; index += 2)
        {
            Xunit.Assert.True(
                index + 1 < translated.Length,
                $"Lane '{lane.Name}' translated to an incomplete MTP filter.");
            switch (translated[index])
            {
                case "--filter-class":
                    included.Add(translated[index + 1]);
                    break;
                case "--filter-not-class":
                    excluded.Add(translated[index + 1]);
                    break;
                case "--filter-not-trait":
                    break;
                default:
                    throw new Xunit.Sdk.XunitException(
                        $"Lane '{lane.Name}' translated to unsupported MTP argument '{translated[index]}'.");
            }
        }

        return (included.Count == 0 || included.Any(pattern => ClassPatternMatches(className, pattern))) &&
               excluded.All(pattern => !ClassPatternMatches(className, pattern));
    }

    private static bool ClassPatternMatches(string className, string pattern)
    {
        Xunit.Assert.StartsWith("*", pattern, StringComparison.Ordinal);
        Xunit.Assert.EndsWith("*", pattern, StringComparison.Ordinal);
        return className.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
    }

    private static string[] FilterClasses(string filter) =>
        filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(operand => operand["FullyQualifiedName~".Length..])
            .ToArray();
}
