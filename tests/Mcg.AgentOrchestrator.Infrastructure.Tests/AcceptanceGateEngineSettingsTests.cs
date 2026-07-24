using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class AcceptanceGateEngineSettingsTests
{
    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_candidate_lane_and_timeout_change_apply_without_engine_recompile")]
    public async Task AcceptanceGateEngineCandidateLaneAndTimeoutChangeApplyWithoutEngineRecompile()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "slotCount": 2,
                "maxConcurrentShards": 2,
                "timeouts": { "defaultMinutes": 3, "buildServerShutdownMinutes": 1 },
                "infrastructureTestLanes": [
                  { "name": "candidate lane", "filter": "FullyQualifiedName~CandidateLaneTests" }
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
            Xunit.Assert.Equal(2, settings.SlotCount);
            Xunit.Assert.Equal(2, settings.MaxConcurrentShards);
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

    [Xunit.Fact(DisplayName = "AcceptanceGateEngine_candidate_MTP_invocation_template_drives_own_gate")]
    public async Task AcceptanceGateEngineCandidateMtpInvocationTemplateDrivesOwnGate()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "slotCount": 1,
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

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_structural_coverage_fails_when_manifest_lane_filters_out_discovered_test")]
    public async Task GoalAcceptanceVerifierStructuralCoverageFailsWhenManifestLaneFiltersOutDiscoveredTest()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": {
                "slotCount": 1,
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
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ]
            }
            """);
        GoalAcceptanceVerifier.ResolveMainWorktreePathForTests = _ => root;
        GoalAcceptanceVerifier.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier((arguments, _, _) =>
            {
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
}
