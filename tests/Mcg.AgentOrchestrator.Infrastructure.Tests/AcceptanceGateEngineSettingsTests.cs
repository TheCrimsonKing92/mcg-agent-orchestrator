using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class AcceptanceGateEngineSettingsTests
{
    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_checked_in_manifest_splits_heavy_lanes_without_narrowing_coverage")]
    public void AcceptanceGateEngineCheckedInManifestSplitsHeavyLanesWithoutNarrowingCoverage()
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());

        Xunit.Assert.Equal(4, settings.MaxConcurrentShards);
        Xunit.Assert.Equal(18, settings.InfrastructureTestLanes.Count);
        Xunit.Assert.All(
            settings.InfrastructureTestLanes,
            lane => Xunit.Assert.True(
                lane.EstimatedSerialSeconds > 0,
                $"Checked-in lane '{lane.Name}' must carry a positive serial-duration estimate."));
        AssertLanePairPreservesCoverage(
            settings,
            "Goal lifecycle commands",
            "Goal worktree cleanup",
            [
                "CliCommandTestsGoalLifecycleCommands",
                "CliCommandTestsPersistentRunnerCommands",
                "CliCommandTestsSubscriptionDispatchCommands",
                "CliCommandTestsTerminalSweepCommands",
                "GoalGitFactIndexTests",
                "GoalsPruneTests",
                "GoalWorktreeTestsAcceptanceLanding",
                "GoalWorktreeTestsCreationResolution",
                "GoalWorktreeTestsOrphanEphemeralSweep",
                "GoalWorktreeTestsRebaseMerge",
                "GoalWorktreeTestsRemoveCleanup",
                "GoalWorktreeTestsSqliteTooling",
                "LandingExecutorTests"
            ]);
        AssertLanePairPreservesCoverage(
            settings,
            "Worker profiles",
            "Worker dispatch fixtures",
            [
                "AdvanceLoopTests",
                "ConductLoopLockTests",
                "ConductorLoopHandoffTests",
                "FirewallSetupCommandTests",
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
                "GoalAcceptanceVerifierTests",
                "GoalAcceptanceVerifierDotnetBuildSlotTests",
                "RealProcessShardAlphaSmokeTests",
                "RealProcessShardBetaSmokeTests",
                "WorkerDispatchJobAccountingTests"
            ]);
    }

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource")]
    public void AcceptanceGateEngineDisabledCollectionsSpanningLanesShareAnExclusiveResource()
    {
        var settings = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot());
        var testAssembly = typeof(AcceptanceGateEngineSettingsTests).Assembly;
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
            if (lanes.Length < 2)
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
            var verifier = new GoalAcceptanceVerifier((arguments, _, timeout, _) =>
            {
                calls.Add((arguments, timeout));
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
            var verifier = new GoalAcceptanceVerifier((_, _, _) =>
                Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1")));

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
            var verifier = new GoalAcceptanceVerifier((arguments, _, _) =>
            {
                calls.Add(arguments);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            });

            var result = await verifier.RunAsync(root);

            Xunit.Assert.True(result.Passed);
            var mtpCall = Xunit.Assert.Single(calls.Where(call =>
                call.Length > 0 &&
                Path.GetFileNameWithoutExtension(call[0]).Equals("Example.Tests", StringComparison.OrdinalIgnoreCase)));
            Xunit.Assert.Contains("--candidate-switch", mtpCall);
            Xunit.Assert.Contains(Path.Combine("candidate", $"Example.Tests{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}"), mtpCall[0]);
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
        var calls = new List<string[]>();
        var buildPermitChecks = 0;
        DotnetBuildEnvironmentLease? buildLease = null;
        GoalAcceptanceVerifier.ResolveMainWorktreePathForTests = _ => root;
        GoalAcceptanceVerifier.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            buildLease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var buildPermitIndex = buildLease.Environment.BuildPermitIndex
                ?? throw new InvalidOperationException("Expected a scheduler-managed build permit.");
            var verifier = new GoalAcceptanceVerifier((arguments, _, _) =>
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
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, displayName));
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
                Xunit.Assert.NotEqual("dotnet", call[0], StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            buildLease?.Dispose();
            GoalAcceptanceVerifier.ResolveMainWorktreePathForTests = null;
            GoalAcceptanceVerifier.ResolveDeletedTestFilesForTests = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_structural_coverage_executes_test_project_omitted_from_manifest")]
    public async Task GoalAcceptanceVerifierStructuralCoverageExecutesTestProjectOmittedFromManifest()
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
            "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        var calls = new List<string[]>();
        GoalAcceptanceVerifier.ResolveMainWorktreePathForTests = _ => root;
        GoalAcceptanceVerifier.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier((arguments, _, _) =>
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

            Xunit.Assert.True(result.Passed);
            Xunit.Assert.Contains(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call.Any(argument =>
                    argument.EndsWith("Added.Tests.csproj", StringComparison.OrdinalIgnoreCase)) &&
                !call.Contains("--list-tests"));
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveMainWorktreePathForTests = null;
            GoalAcceptanceVerifier.ResolveDeletedTestFilesForTests = null;
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
        GoalAcceptanceVerifier.ResolveMainWorktreePathForTests = _ => root;
        GoalAcceptanceVerifier.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier((arguments, _, _) =>
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
            GoalAcceptanceVerifier.ResolveMainWorktreePathForTests = null;
            GoalAcceptanceVerifier.ResolveDeletedTestFilesForTests = null;
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

    private static void WriteVstestTrx(string[] arguments, string testName)
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
        File.WriteAllText(
            Path.Combine(resultsDirectory, logger[prefix.Length..]),
            $"<TestRun><TestDefinitions><UnitTest id=\"1\" name=\"{testName}\"><TestMethod className=\"{testClass}\" name=\"{method}\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"1\" testName=\"{testName}\" outcome=\"Passed\" /></Results></TestRun>");
    }

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
            $"<TestRun><TestDefinitions><UnitTest id=\"1\" name=\"{displayName}\"><TestMethod className=\"{testClass}\" name=\"{method}\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"1\" testName=\"{displayName}\" outcome=\"Passed\" /></Results></TestRun>");
    }

    private static void AssertLanePairPreservesCoverage(
        AcceptanceGateEngineSettings settings,
        string firstLaneName,
        string secondLaneName,
        IReadOnlyList<string> expectedClasses)
    {
        var first = settings.InfrastructureTestLanes.Single(lane => lane.Name == firstLaneName);
        var second = settings.InfrastructureTestLanes.Single(lane => lane.Name == secondLaneName);
        var firstClasses = FilterClasses(first.Filter);
        var secondClasses = FilterClasses(second.Filter);

        Xunit.Assert.Empty(firstClasses.Intersect(secondClasses, StringComparer.Ordinal));
        Xunit.Assert.Equal(
            expectedClasses.Order(StringComparer.Ordinal),
            firstClasses.Concat(secondClasses).Order(StringComparer.Ordinal));
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
