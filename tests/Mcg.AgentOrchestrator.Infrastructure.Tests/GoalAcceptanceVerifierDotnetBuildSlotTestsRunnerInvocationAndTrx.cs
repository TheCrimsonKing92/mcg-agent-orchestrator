using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRunnerInvocationAndTrx : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [OptInRealAcceptanceVerifierFact(DisplayName = "GoalAcceptanceVerifier_real_runner_smoke_is_opt_in")]
    [Xunit.Trait("Category", "AcceptanceOptIn")]
    public async Task GoalAcceptanceVerifierRealRunnerSmokeIsOptIn()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "dotnet info smoke", "type": "command", "command": "dotnet", "arguments": ["--info"], "timeoutMinutes": 1 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier();

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed, result.OutputTail);
            Assert.Contains(result.Checks!, check => check.Name == "dotnet info smoke" && check.Passed);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_invokes_dotnet_test_with_isolated_script_arguments")]
    public async Task GoalAcceptanceVerifierInvokesDotnetTestWithIsolatedScriptArguments()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "full dotnet tests", "type": "dotnet-test", "project": "Mcg.AgentOrchestrator.sln", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            });

            var goalId = new GoalId("abcdef12abcdef12abcdef12abcdef12");
            var result = await verifier.RunAsync(root, goalId);

            Assert.True(result.Passed);
            Assert.Equal(2, calls.Count);
            Assert.True(calls[0].SequenceEqual(["dotnet", "build-server", "shutdown"]));

            var dotnetArgs = calls[1];
            AssertIsolatedTestCommand(dotnetArgs);
            Assert.Equal("Mcg.AgentOrchestrator.sln", dotnetArgs[2]);
            Assert.Contains("--filter", dotnetArgs);
            Assert.Contains("FullyQualifiedName!~DashboardHostTests&Category!=HostIntegration", dotnetArgs);
            Assert.Contains("--blame-hang-timeout", dotnetArgs);
            Assert.Contains("120s", dotnetArgs);
            Assert.Equal("goal-abcdef12", result.Checks!.Single().LeaseId);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_focused_evidence_uses_slot_routed_mtp_executable")]
    public async Task GoalAcceptanceVerifierFocusedEvidenceUsesSlotRoutedMtpExecutable()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                "Infrastructure.Tests: ConductorDriverTests,GoalAcceptanceVerifierTests",
                stableSlotIndex: 0);

            Assert.True(result.Accepted);
            Assert.True(result.Passed);
            // The focused-evidence target projects are MTP, so the run must route through the MTP
            // executable, NOT `dotnet test` (which fails against MTP projects on .NET 10).
            Assert.DoesNotContain(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test");
            var mtpCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
            Assert.Contains("--report-trx", mtpCall);
            Assert.Contains("--filter-class", mtpCall);
            Assert.Equal("goal-abcdef12", result.Checks.Single().LeaseId);
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_uses_slot_executable_trx_filters_and_coexists_with_vstest")]
    public async Task GoalAcceptanceVerifierMtpRunnerUsesSlotExecutableTrxFiltersAndCoexistsWithVstest()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~GoalLifecycleTests&FullyQualifiedName!~SlowCoreTests&Category!=HostIntegration"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3."));
                }

                return Task.FromResult(args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                    ? new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2.")
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var artifactsPath = DotnetBuildEnvironmentManager.GoalArtifactsPath(
                new GoalId("abcdef12abcdef12abcdef12abcdef12"));
            Assert.Contains(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build");
            Assert.Contains(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
            Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2].Contains("Mcg.AgentOrchestrator.Core.Tests", StringComparison.Ordinal));

            var mtpCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
            Assert.Equal("dotnet", mtpCall[0], ignoreCase: true);
            Assert.Equal(
                Path.Combine(artifactsPath, "bin", "Mcg.AgentOrchestrator.Core.Tests", "debug", "Mcg.AgentOrchestrator.Core.Tests.dll"),
                mtpCall[1],
                ignoreCase: true);
            AssertArgumentPair(mtpCall, "--filter-class", "*GoalLifecycleTests*");
            AssertArgumentPair(mtpCall, "--filter-not-class", "*SlowCoreTests*");
            Assert.Contains("--filter-not-trait", mtpCall);
            Assert.Contains("Category=HostIntegration", mtpCall);
            Assert.Contains("--results-directory", mtpCall);
            Assert.Contains("--report-trx", mtpCall);
            Assert.Contains("--report-trx-filename", mtpCall);
            Assert.Single(result.Checks!.Single(check => check.Name == "core tests").TestResultPaths!);
            Assert.True(File.Exists(result.Checks!.Single(check => check.Name == "core tests").TestResultPaths!.Single()));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_unfiltered_mtp_dashboard_run_excludes_host_integration")]
    public async Task GoalAcceptanceVerifierUnfilteredMtpDashboardRunExcludesHostIntegration()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "full dotnet tests: dashboard", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Dashboard.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2."));
                }

                return Task.FromResult(args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                    ? new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2.")
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var mtpCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Dashboard.Tests"));
            AssertArgumentPair(mtpCall, "--filter-not-trait", "Category=HostIntegration");
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_extracts_all_failed_tests_from_real_mtp_trx_fixture")]
    public void GoalAcceptanceVerifierExtractsAllFailedTestsFromRealMtpTrxFixture()
    {
        var fixturePath = MtpFailureFixturePath();

        var evidence = GoalAcceptanceVerifier.ExtractTrxFailureEvidence(fixturePath);

        Assert.Equal(
            [
                "[FAIL] Retry evidence includes failed test names: Assert.Contains() Failure: Sub-string not found",
                "[FAIL] MTP shard preserves theory display name(value: 42): Expected shard count to be 2, but found 1"
            ],
            evidence);
        Assert.DoesNotContain(evidence, line =>
            line.Contains("Passing MTP test is not surfaced", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "AcceptanceTrxFailureReader_preserves_full_nonpassing_evidence")]
    public void AcceptanceTrxFailureReaderPreservesFullNonpassingEvidence()
    {
        var fixturePath = MtpFailureFixturePath();

        var receipt = AcceptanceTrxFailureReader.Read(fixturePath);

        Assert.Equal(AcceptanceTrxReadStatus.Readable, receipt.Status);
        Assert.Equal(fixturePath, receipt.Path);
        Assert.Equal(3, receipt.Failures.Count);
        var assertion = receipt.Failures[0];
        Assert.Equal(
            "Mcg.AgentOrchestrator.Infrastructure.Tests.RetryEvidenceTests.IncludesFailures",
            assertion.TestName);
        Assert.Equal(
            "Assert.Contains() Failure: Sub-string not found\n" +
            "String:    \"infrastructure tests: Cli: failed: 1\"\n" +
            "Not found: \"RetryEvidenceTests.IncludesFailures\"\n" +
            "Expected: 2\n" +
            "Actual:   0",
            assertion.Message);
        Assert.Equal(
            "at Mcg.AgentOrchestrator.Infrastructure.Tests.RetryEvidenceTests.IncludesFailures() in D:\\a\\mcg-agent-orchestrator\\RetryEvidenceTests.cs:line 42",
            assertion.StackTrace);
        Assert.Equal(
            "Mcg.AgentOrchestrator.Infrastructure.Tests.MtpShardTests.PreservesTheoryDisplayName",
            receipt.Failures[1].TestName);
        Assert.Equal("Timeout", receipt.Failures[2].Outcome);
        Assert.Contains("30 second partition timeout", receipt.Failures[2].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(receipt.Failures, failure =>
            failure.TestName?.Contains("Passing MTP test is not surfaced", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failed_mtp_shard_surfaces_fixture_test_names_and_messages")]
    public async Task GoalAcceptanceVerifierFailedMtpShardSurfacesFixtureTestNamesAndMessages()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, "TestResults", "mtp-fixture"));
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args, MtpFailureFixturePath());
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 2, Passed: 1, Skipped: 0, Total: 3."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Assert.False(result.Passed);
            var output = Assert.Single(result.Checks!).OutputTail;
            Assert.Contains(
                "[FAIL] Retry evidence includes failed test names: Assert.Contains() Failure: Sub-string not found",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "[FAIL] MTP shard preserves theory display name(value: 42): Expected shard count to be 2, but found 1",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Passing MTP test is not surfaced", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_failed_mtp_shard_explains_missing_or_empty_trx")]
    [Xunit.InlineData(false, "failed — no TRX produced (shard was killed or crashed before reporter flushed)")]
    [Xunit.InlineData(true, "failed — TRX found but contained no failure records (process may have exited before tests ran)")]
    public async Task GoalAcceptanceVerifierFailedMtpShardExplainsMissingOrEmptyTrx(
        bool writeEmptyTrx,
        string expectedEvidence)
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, "TestResults", writeEmptyTrx ? "empty-trx" : "missing-trx"));
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    if (writeEmptyTrx)
                    {
                        WriteMtpTrx(args);
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Assert.False(result.Passed);
            Assert.Contains(
                $"[FAIL] infrastructure tests: Cli: {expectedEvidence}",
                Assert.Single(result.Checks!).OutputTail,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failed_mtp_shard_does_not_reuse_stale_trx")]
    public async Task GoalAcceptanceVerifierFailedMtpShardDoesNotReuseStaleTrx()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            var attemptPrefix = Path.Combine(root, "TestResults", "reused-stable-slot");
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);
            var staleTrxPath = $"{attemptPrefix}.infrastructure-tests-cli.trx";
            Directory.CreateDirectory(Path.GetDirectoryName(staleTrxPath)!);
            File.Copy(MtpFailureFixturePath(), staleTrxPath);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Test process exited before the reporter flushed."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Assert.False(result.Passed);
            var output = Assert.Single(result.Checks!).OutputTail;
            Assert.Contains(
                "[FAIL] infrastructure tests: Cli: failed — no TRX produced (shard was killed or crashed before reporter flushed)",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Retry evidence includes failed test names", output, StringComparison.Ordinal);
            Assert.False(File.Exists(staleTrxPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_vstest_failure_stdout_remains_unchanged")]
    public async Task GoalAcceptanceVerifierVstestFailureStdoutRemainsUnchanged()
    {
        const string failureOutput =
            "[xUnit.net 00:00:01.23]     Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]\n" +
            "Assert.True() Failure";
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
                Task.FromResult(args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                    ? new GoalAcceptanceVerifier.CommandResult(1, failureOutput)
                    : new GoalAcceptanceVerifier.CommandResult(0, "")));

            var result = await verifier.RunAsync(root);

            Assert.False(result.Passed);
            Assert.Equal(failureOutput, Assert.Single(result.Checks!).OutputTail);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_omits_total_session_timeout")]
    public async Task GoalAcceptanceVerifierMtpRunnerOmitsTotalSessionTimeout()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var mtpCall = calls.Single(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
            Assert.DoesNotContain(mtpCall, argument => argument.Equals("--timeout", StringComparison.OrdinalIgnoreCase));
            var longRunningIndex = Array.IndexOf(mtpCall, "--long-running");
            Assert.True(longRunningIndex >= 0);
            Assert.True(longRunningIndex + 1 < mtpCall.Length);
            Assert.Equal("120", mtpCall[longRunningIndex + 1]);
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_infrastructure_remainder_shard_uses_slot_executable")]
    public async Task GoalAcceptanceVerifierMtpRunnerInfrastructureRemainderShardUsesSlotExecutable()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(args.Length >= 3 &&
                    args[0] == "dotnet" &&
                    args[1] == "test" &&
                    args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                    ? new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback")
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var shardChecks = result.Checks!
                .Where(check => check.Name.StartsWith("infrastructure tests: ", StringComparison.Ordinal))
                .ToArray();
            var mtpCalls = calls
                .Where(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                .ToArray();
            Assert.Equal(shardChecks.Length, mtpCalls.Length);
            Assert.Contains(mtpCalls, call => HasArgumentPair(call, "--filter-class", "*CliCommandTests*"));
            Assert.Contains(mtpCalls, call =>
                HasArgumentPair(call, "--filter-not-class", "*CliCommandTests*") &&
                HasArgumentPair(call, "--filter-not-class", "*GoalAcceptanceVerifierTests*"));
            Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dotnet_test_without_runner_uses_vstest")]
    public async Task GoalAcceptanceVerifierDotnetTestWithoutRunnerUsesVstest()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(args.Length >= 2 && args[0] == "dotnet" && args[1] == "test"
                    ? new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            Assert.Contains(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" &&
                call.Contains("--no-build"));
            Assert.DoesNotContain(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dotnet_test_unknown_runner_fails_loudly")]
    public async Task GoalAcceptanceVerifierDotnetTestUnknownRunnerFailsLoudly()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "typo", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "should not run tests"));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcdef12abcdef12abcdef12abcdef12"),
                stableSlotIndex: 0);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!);
            Assert.False(check.Passed);
            Assert.Contains("unrecognized runner 'typo'", check.OutputTail);
            Assert.DoesNotContain(calls, call => call.Length > 1 && call[0] == "dotnet" && call[1] == "test");
            Assert.DoesNotContain(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_mtp_runner_without_build_phase_builds_project_before_executable")]
    public async Task GoalAcceptanceVerifierMtpRunnerWithoutBuildPhaseBuildsProjectBeforeExecutable()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("cccccccccccccccccccccccccccccccc");
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.True(result.Passed);
            var buildIndex = calls.FindIndex(call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "build" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj");
            var mtpIndex = calls.FindIndex(call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
            Assert.True(buildIndex >= 0);
            Assert.True(mtpIndex > buildIndex);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

}
