using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsPolicyShardScope : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_selects_infrastructure_tests_from_default_plan")]
    public async Task GoalAcceptanceVerifierSelectsInfrastructureTestsFromDefaultPlan()
    {
        var calls = new List<string[]>();
        var root = CreatePartitionedInfrastructureManifestWorkspace();
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
            changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"]);

        Assert.True(result.Passed);
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = CountChangeScopedInfrastructureTestLanes(root);
        Assert.Equal(laneCount, infrastructureCalls.Length);
        Assert.DoesNotContain(calls, call => call.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparer.Ordinal));
        foreach (var call in infrastructureCalls)
        {
            Assert.True(
                call.Contains("--filter-class") || call.Contains("--filter-not-class"),
                "Each infrastructure shard must carry a translated MTP class filter.");
            Assert.Contains("--report-trx", call);
        }

        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Worker profiles");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        var infrastructureRollup = Assert.Single(result.Checks!, check => check.Name == "infrastructure tests");
        Assert.True(infrastructureRollup.Passed);
        Assert.Equal(
            $"covered by {laneCount} partitioned checks; changed file in dependency closure; " +
            "changed projects: Infrastructure; dependency closure: App, Dashboard.Tests, Infrastructure, " +
            "Infrastructure.Acceptance.Tests, Infrastructure.Cli.Tests, Infrastructure.ProviderEnvironment.Tests, " +
            "Infrastructure.Tests, TestSupport",
            infrastructureRollup.ResultSummary);
        Assert.Equal(
            result.Checks
                .Where(check => check.Name.StartsWith("infrastructure tests: ", StringComparison.Ordinal))
                .Select(check => check.Name),
            infrastructureRollup.CoveredBy);
        DeleteDirectoryWithRetry(root);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failed_partition_rollup_records_structured_coverer_without_changing_summary")]
    public async Task GoalAcceptanceVerifierFailedPartitionRollupRecordsStructuredCovererWithoutChangingSummary()
    {
        var root = CreatePartitionedInfrastructureManifestWorkspace();
        var failingLaneName = "Worker profiles";
        string[] changedFiles = ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"];
        var policyShardPlan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(changedFiles);
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    var fails = HasArgumentPair(args, "--filter-class", "*WorkerProfileTests*");
                    WriteMtpTrx(args, fails ? MtpFailureFixturePath() : null);
                    return Task.FromResult(fails
                        ? new GoalAcceptanceVerifier.CommandResult(1, "Worker profiles partition failed.")
                        : new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                changedFiles: changedFiles);

            Assert.False(result.Passed);
            var failedShard = Assert.Single(result.Checks!, check => check.Name == $"infrastructure tests: {failingLaneName}");
            Assert.False(failedShard.Passed);
            var rollup = Assert.Single(result.Checks, check => check.Name == "infrastructure tests");
            Assert.False(rollup.Passed);
            Assert.Equal(
                $"covered by failed partition: {failedShard.Name}; changed file in dependency closure; {policyShardPlan.Evidence}",
                rollup.ResultSummary);
            Assert.Equal([failedShard.Name], rollup.CoveredBy);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_focused_infrastructure_filter_for_cli_only_changes")]
    public async Task GoalAcceptanceVerifierRunsFocusedInfrastructureFilterForCliOnlyChanges()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Focused CLI tests passed. Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2."));
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
            changedFiles: ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs"]);

        Assert.True(result.Passed);
        // Whitespace git-diff + focused shard (dotnet build + MTP executable); no extra runs.
        Assert.Equal(3, calls.Count);
        var focusedCall = Assert.Single(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.DoesNotContain(focusedCall, argument => argument.Contains("Mcg.AgentOrchestrator.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--filter-class", focusedCall);
        AssertArgumentPair(focusedCall, "--filter-class", "*CliHelpTests*");
        AssertArgumentPair(focusedCall, "--filter-class", "*CliCommandTests*");
        Assert.False(focusedCall.Any(argument => argument.Contains("FundamentalAliasTests", StringComparison.Ordinal)));
        Assert.DoesNotContain(focusedCall, argument => argument.Contains("DashboardHostTests", StringComparison.Ordinal));
        Assert.Contains(result.Checks!, check => check.Name == "focused CLI infrastructure tests");
        Assert.Contains(result.Checks!, check =>
            check.Name == "core tests" &&
            check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.ResultSummary?.Contains("covered by: focused CLI infrastructure tests", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_skips_core_policy_shard_for_infrastructure_test_only_scope")]
    public async Task GoalAcceptanceVerifierSkipsCorePolicyShardForInfrastructureTestOnlyScope()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Focused CLI tests passed. Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            if (args.Length >= 2 && args[0] == "git" && args[1] == "diff")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
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
            changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs"]);

        Assert.True(result.Passed);
        // Whitespace git-diff + focused shard (dotnet build + MTP executable) + the two
        // tamper-guard git diffs (name-only, then the per-file unified diff); the core policy shard is skipped.
        Assert.Equal(5, calls.Count);
        Assert.DoesNotContain(calls, call => call.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparer.OrdinalIgnoreCase));
        var focusedCall = Assert.Single(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.Contains("--filter-class", focusedCall);
        AssertArgumentPair(focusedCall, "--filter-class", "*WorkerProfileTests*");
        Assert.DoesNotContain(
            calls,
            call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests") &&
                !call.Contains("--filter-class") &&
                !call.Contains("--filter-not-class"));
        var coreReceipt = result.Checks!.Single(check => check.Name == "core tests");
        Assert.True(coreReceipt.Passed);
        Assert.Contains("skipped: no changed file in dependency closure", coreReceipt.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("changed projects: Infrastructure.Tests", coreReceipt.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("dependency closure: Infrastructure.Tests", coreReceipt.ResultSummary, StringComparison.Ordinal);
        var infrastructureReceipt = result.Checks.Single(check => check.Name == "infrastructure tests");
        Assert.Contains("covered by: focused changed infrastructure tests", infrastructureReceipt.ResultSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_skips_extracted_project_for_unrelated_parent_test_change")]
    public async Task GoalAcceptanceVerifierSkipsExtractedProjectForUnrelatedParentTestChange()
    {
        var root = CreateExtractedProjectManifestWorkspace();
        var calls = new List<string[]>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs"]);

            Assert.True(result.Passed);
            Assert.DoesNotContain(
                calls,
                call => call.Any(argument => argument.Contains(
                    "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests",
                    StringComparison.OrdinalIgnoreCase)));
            Assert.DoesNotContain(
                calls,
                call => call.Any(argument => argument.Contains(
                    "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests",
                    StringComparison.OrdinalIgnoreCase)));
            var providerReceipt = Assert.Single(result.Checks!, check => check.Name == "provider environment tests");
            Assert.True(providerReceipt.Passed);
            Assert.Contains(
                "skipped: no changed file in dependency closure",
                providerReceipt.ResultSummary,
                StringComparison.Ordinal);
            Assert.Contains("dependency closure: Infrastructure.Tests", providerReceipt.ResultSummary, StringComparison.Ordinal);
            var cliReceipt = Assert.Single(result.Checks, check => check.Name == "cli tests");
            Assert.True(cliReceipt.Passed);
            Assert.Contains(
                "skipped: no changed file in dependency closure",
                cliReceipt.ResultSummary,
                StringComparison.Ordinal);
            Assert.Contains("dependency closure: Infrastructure.Tests", cliReceipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_routes_provider_source_changes_through_typed_dependency_closure")]
    public async Task GoalAcceptanceVerifierRoutesProviderSourceChangesThroughTypedDependencyClosure()
    {
        var root = CreateExtractedProjectManifestWorkspace();
        var calls = new List<string[]>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") ||
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests") ||
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure.Providers/ModelProviders.cs"]);

            Assert.True(result.Passed);
            Assert.Single(
                calls,
                call => IsMtpExecutableCall(
                    call,
                    "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests"));
            var providerReceipt = Assert.Single(
                result.Checks!,
                check => check.Name == "provider environment tests");
            Assert.True(providerReceipt.Passed);
            Assert.DoesNotContain(
                "skipped: no changed file in dependency closure",
                providerReceipt.ResultSummary,
                StringComparison.Ordinal);
            Assert.Contains(
                "changed projects: Infrastructure.Providers",
                providerReceipt.ResultSummary,
                StringComparison.Ordinal);
            Assert.Contains(
                "dependency closure:",
                providerReceipt.ResultSummary,
                StringComparison.Ordinal);
            Assert.Contains(
                "Infrastructure.ProviderEnvironment.Tests",
                providerReceipt.ResultSummary,
                StringComparison.Ordinal);
            var coreReceipt = Assert.Single(result.Checks, check => check.Name == "core tests");
            Assert.Contains(
                "skipped: no changed file in dependency closure",
                coreReceipt.ResultSummary,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_includes_cli_project_for_related_change")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliArgumentNormalizationTests.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Cli/CliArgumentParser.cs")]
    public async Task GoalAcceptanceVerifierIncludesCliProjectForRelatedChange(string changedFile)
    {
        var root = CreateExtractedProjectManifestWorkspace();
        var calls = new List<string[]>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") ||
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests") ||
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, changedFiles: [changedFile]);

            Assert.True(result.Passed);
            Assert.Single(
                calls,
                call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests"));
            var cliReceipt = Assert.Single(result.Checks!, check => check.Name == "cli tests");
            Assert.True(cliReceipt.Passed);
            Assert.DoesNotContain(
                "skipped: no changed file in dependency closure",
                cliReceipt.ResultSummary,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_project_specific_filters_for_multiple_mapped_app_subsystems")]
    public async Task GoalAcceptanceVerifierRunsProjectSpecificFiltersForMultipleMappedAppSubsystems()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") ||
                IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Dashboard.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Focused App tests passed. Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6."));
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
            changedFiles:
            [
                "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs",
                "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"
            ]);

        Assert.True(result.Passed);
        var infrastructureCall = Assert.Single(calls, candidate => IsMtpExecutableCall(candidate, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.DoesNotContain(calls, candidate => candidate.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        AssertArgumentPair(infrastructureCall, "--filter-class", "*CliCommandTests*");
        AssertArgumentPair(infrastructureCall, "--filter-class", "*CliHelpTests*");
        Assert.Contains(result.Checks!, check => check.Name == "focused CLI infrastructure tests");
        Assert.DoesNotContain(result.Checks!, check => check.Name == "focused dashboard infrastructure tests");
        var infrastructureReceipt = Assert.Single(result.Checks!, check => check.Name == "infrastructure tests");
        Assert.True(infrastructureReceipt.Passed);
        Assert.Contains(
            "covered by: focused CLI infrastructure tests",
            infrastructureReceipt.ResultSummary ?? "",
            StringComparison.Ordinal);
        Assert.Contains(
            "changed file in dependency closure",
            infrastructureReceipt.ResultSummary ?? "",
            StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_safety_valves_force_full_policy_shards")]
    public async Task GoalAcceptanceVerifierSafetyValvesForceFullPolicyShards()
    {
        var root = CreateCheckedInManifestShapeWorkspace();

        static async Task AssertFullShardRunAsync(string root, IReadOnlyList<string> changedFiles)
        {
            var calls = new List<string[]>();
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Infrastructure partition passed."
                        : ""));
            });

            var result = await verifier.RunAsync(root, changedFiles: changedFiles);

            Assert.True(result.Passed);
            Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[1][2]);
            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
                .ToArray();
            var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
            Assert.Equal(laneCount, infrastructureCalls.Length);
            foreach (var call in infrastructureCalls)
                Assert.Contains("--filter", call);
            Assert.DoesNotContain(infrastructureCalls, call => !call.Contains("--filter"));
            Assert.DoesNotContain(result.Checks!, check =>
                check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
            Assert.Contains(result.Checks!, check =>
                check.Name == "infrastructure tests" &&
                check.Passed &&
                check.ResultSummary == $"covered by {laneCount} partitioned checks");
            Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
            Assert.Contains(calls, call =>
                call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj");
            Assert.Contains(result.Checks!, check =>
                check.Name == "dashboard tests" &&
                check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) != true);
        }

        await AssertFullShardRunAsync(
            root,
            [
                "scripts/Invoke-IsolatedDotnet.ps1"
            ]);
        await AssertFullShardRunAsync(
            root,
            [
                "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj"
            ]);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_manifest_command_checks_before_dotnet_tests")]
    public async Task GoalAcceptanceVerifierRunsManifestCommandChecksBeforeDotnetTests()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "whitespace", "type": "command", "command": "git", "arguments": [ "diff", "--check" ] },
                { "name": "focused tests", "type": "dotnet-test", "project": "tests/Example.Tests.csproj", "arguments": [ "--filter", "Example", "--verbosity", "minimal" ] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Tests passed.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].SequenceEqual(["git", "diff", "--check"]));
        Assert.Equal("dotnet", calls[1][0]);
        Assert.Equal("test", calls[1][1]);
        Assert.Equal("tests/Example.Tests.csproj", calls[1][2]);
        Assert.True(calls[1].Contains("--filter", StringComparer.Ordinal));
        Assert.Equal(2, result.Checks!.Count);
        Assert.Equal("whitespace", result.Checks[0].Name);
        Assert.Equal("focused tests", result.Checks[1].Name);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_brokers_dotnet_manifest_command_checks")]
    public async Task GoalAcceptanceVerifierBrokersDotnetManifestCommandChecks()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "custom dotnet", "type": "command", "command": "dotnet", "arguments": [ "test", "tests/Example.Tests.csproj", "--verbosity", "minimal" ] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root, new GoalId("13572468135724681357246813572468"));

        Assert.True(result.Passed);
        Assert.Single(calls);
        Assert.Equal("dotnet", calls[0][0]);
        Assert.Equal("test", calls[0][1]);
        AssertIsolatedTestCommand(calls[0]);
        var check = Xunit.Assert.Single(result.Checks!);
        Assert.Equal("custom dotnet", check.Name);
        Assert.Equal("goal-13572468", check.LeaseId);
        Assert.Equal("goal-acceptance-verifier", check.BrokerName);
        Assert.True(check.ResultSummary?.Contains("Failed: 0", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_defers_granular_csproj_checks_to_solution_wide_run")]
    public async Task GoalAcceptanceVerifierDefersGranularCsprojChecksToSolutionWideRun()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Infra.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].SequenceEqual(["git", "diff", "--check"]));
        AssertIsolatedTestCommand(calls[1]);
        Assert.False(calls[1].Any(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(4, result.Checks!.Count);

        var fullCheck = result.Checks.Single(c => c.Name == "full dotnet tests");
        Assert.True(fullCheck.Passed);
        Assert.True(fullCheck.ResultSummary?.Contains("Passed:", StringComparison.Ordinal) == true);

        var coreCheck = result.Checks.Single(c => c.Name == "core tests");
        Assert.True(coreCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", coreCheck.ResultSummary);
        Assert.Equal([fullCheck.Name], coreCheck.CoveredBy);
        Assert.Equal(fullCheck.ArtifactsPath, coreCheck.ArtifactsPath);
        Assert.Equal(fullCheck.LeaseId, coreCheck.LeaseId);

        var infraCheck = result.Checks.Single(c => c.Name == "infrastructure tests");
        Assert.True(infraCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", infraCheck.ResultSummary);
        Assert.Equal([fullCheck.Name], infraCheck.CoveredBy);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failing_solution_run_marks_deferred_checks_failed")]
    public async Task GoalAcceptanceVerifierFailingSolutionRunMarksDeferredChecksFailed()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Infra.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "Failed! - Failed: 2, Passed: 3, Skipped: 0, Total: 5.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, calls.Count);
        Assert.Equal(4, result.Checks!.Count);

        var fullCheck = result.Checks.Single(c => c.Name == "full dotnet tests");
        Assert.False(fullCheck.Passed);

        var coreCheck = result.Checks.Single(c => c.Name == "core tests");
        Assert.False(coreCheck.Passed);
        Assert.Equal(1, coreCheck.ExitCode);
        Assert.Equal("covered by: full dotnet tests", coreCheck.ResultSummary);
        Assert.Equal(fullCheck.OutputTail, coreCheck.OutputTail);

        var infraCheck = result.Checks.Single(c => c.Name == "infrastructure tests");
        Assert.False(infraCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", infraCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_defers_sln_project_check_using_sln_file_content")]
    public async Task GoalAcceptanceVerifierDefersSlnProjectCheckUsingSlnFileContent()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "extra tests", "type": "dotnet-test", "project": "tests/NotInSolution.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "project": "Fake.sln", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        File.WriteAllText(Path.Combine(root, "Fake.sln"),
            "Project(\"{FAE04EC0}\") = \"Core.Tests\", \"tests/Core.Tests.csproj\", \"{GUID}\"\nEndProject\n");

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, "Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3."),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        // Solution run + extra tests (not in solution).
        Assert.Equal(2, calls.Count);
        Assert.Equal(3, result.Checks!.Count);

        Assert.True(result.Checks.Single(c => c.Name == "full dotnet tests").Passed);
        Assert.Equal("covered by: full dotnet tests",
            result.Checks.Single(c => c.Name == "core tests").ResultSummary);
        // "extra tests" ran for real (not synthesized), so ResultSummary is from actual output
        Assert.False(result.Checks.Single(c => c.Name == "extra tests").ResultSummary
            ?.StartsWith("covered by:", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_injects_policy_required_checks_missing_from_manifest")]
    public async Task GoalAcceptanceVerifierInjectsPolicyRequiredChecksMissingFromManifest()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var coreTestsProject = Path.Combine(
            root,
            "tests",
            "Mcg.AgentOrchestrator.Core.Tests",
            "Mcg.AgentOrchestrator.Core.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(coreTestsProject)!);
        File.WriteAllText(
            coreTestsProject,
            "<Project><PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5."));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        });

        // Core source file -> impact plan requires only "core tests".
        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        // Whitespace git-diff + the injected core shard (dotnet build + MTP executable).
        Assert.Equal(3, calls.Count);
        Assert.True(calls[0].SequenceEqual(["git", "diff", "--check"]));
        // Core.Tests is an MTP project, so the injected "core tests" check runs the MTP executable
        // (preceded by a dotnet build), never the VSTest `dotnet test` runner.
        Assert.Single(calls, call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests"));
        Assert.DoesNotContain(calls, call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test");
        Assert.Equal(2, result.Checks!.Count);
        Assert.Equal("whitespace", result.Checks[0].Name);
        Assert.True(result.Checks.Any(c => c.Name == "core tests" && c.Passed));
        Assert.False(result.Checks.Any(c => c.Name == "infrastructure tests"));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_core_and_dependent_infrastructure_shards_for_core_scope")]
    public async Task GoalAcceptanceVerifierRunsCoreAndDependentInfrastructureShardsForCoreScope()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                    ? "Tests passed."
                    : ""));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(
            CountChangeScopedInfrastructureTestLanes(root) + 4,
            calls.Count);
        Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[1][2]);
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        var laneCount = CountChangeScopedInfrastructureTestLanes(root);
        Assert.Equal(laneCount, infrastructureCalls.Length);
        foreach (var call in infrastructureCalls)
            Assert.Contains("--filter", call);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(result.Checks!, check => check.Name == "core tests");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary?.StartsWith(
                $"covered by {laneCount} partitioned checks; changed file in dependency closure; ",
                StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_substitutes_solution_check_with_partitioned_infrastructure_checks_for_infra_scope")]
    public async Task GoalAcceptanceVerifierSubstitutesSolutionCheckWithPartitionedInfrastructureChecksForInfraScope()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Infrastructure partition passed. Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
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
            changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"]);

        Assert.True(result.Passed);
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = CountChangeScopedInfrastructureTestLanes(root);
        Assert.Equal(laneCount, infrastructureCalls.Length);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        foreach (var call in infrastructureCalls)
            Assert.True(
                call.Contains("--filter-class") || call.Contains("--filter-not-class"),
                "Each infrastructure shard must carry a translated MTP class filter.");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Goal acceptance verifier");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary?.StartsWith(
                $"covered by {laneCount} partitioned checks; changed file in dependency closure; ",
                StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_substitutes_solution_check_with_union_for_core_and_infra_scope")]
    public async Task GoalAcceptanceVerifierSubstitutesSolutionCheckWithUnionForCoreAndInfraScope()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Tests passed. Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }

            // The substituted infrastructure shards must route through the MTP executable; a
            // `dotnet test` against the Infrastructure.Tests project would be a VSTest regression.
            if (args.Length >= 3 &&
                args[0] == "dotnet" &&
                args[1] == "test" &&
                args[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            {
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "unexpected vstest fallback"));
            }

            // The manifest "core tests" check carries no explicit runner, so it stays on VSTest
            // `dotnet test`; only synthesized/policy checks are promoted to runner=mtp.
            return Task.FromResult(args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                ? new GoalAcceptanceVerifier.CommandResult(0, "Tests passed.")
                : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles:
            [
                "src/Mcg.AgentOrchestrator.Core/Application/Foo.cs",
                "src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"
            ]);

        Assert.True(result.Passed);
        // The manifest "core tests" check has no explicit runner, so it runs via VSTest `dotnet test`.
        Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[1][2]);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        var infrastructureCalls = calls
            .Where(IsInfrastructurePartitionTestCall)
            .ToArray();
        var laneCount = CountChangeScopedInfrastructureTestLanes(root);
        Assert.Equal(laneCount, infrastructureCalls.Length);
        // The substituted infrastructure shards are MTP, so every shard carries a translated class filter.
        foreach (var call in infrastructureCalls)
            Assert.True(
                IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests") &&
                (call.Contains("--filter-class") || call.Contains("--filter-not-class")),
                "Each infrastructure shard must run the MTP executable with a translated class filter.");
        Assert.Contains(result.Checks!, check => check.Name == "core tests");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Cli");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary?.StartsWith(
                $"covered by {laneCount} partitioned checks; changed file in dependency closure; ",
                StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_keeps_solution_check_for_build_security_broad_or_disabled_scopes")]
    public async Task GoalAcceptanceVerifierKeepsSolutionCheckForBuildSecurityBroadOrDisabledScopes()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Full tests passed.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root, changedFiles: ["Directory.Build.props"]);

        Assert.True(result.Passed);
        Assert.Contains("Mcg.AgentOrchestrator.sln", calls[1], StringComparer.OrdinalIgnoreCase);
        Assert.True(result.Checks!.Any(check => check.Name == "full dotnet tests"));
        Assert.True(result.Checks.Any(check => check.ResultSummary == "covered by: full dotnet tests"));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_change_scoped_kill_switch_keeps_solution_check")]
    public async Task GoalAcceptanceVerifierChangeScopedKillSwitchKeepsSolutionCheck()
    {
        var variables = new Dictionary<string, string?>();
        using var policyScope = AcceptanceShardPolicySwitches.Use(new(ReadVariable: variables.GetValueOrDefault));
        var previous = variables.GetValueOrDefault("MCG_ACCEPTANCE_CHANGE_SCOPED");
        variables["MCG_ACCEPTANCE_CHANGE_SCOPED"] = "0";
        try
        {
            var root = CreateStandardManifestWorkspace();
            var calls = new List<string[]>();
            var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
                new(0, ""),
                new(0, "Full tests passed.")
            ]);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            var result = await verifier.RunAsync(
                root,
                changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

            Assert.True(result.Passed);
            Assert.Contains("Mcg.AgentOrchestrator.sln", calls[1], StringComparer.OrdinalIgnoreCase);
            Assert.True(result.Checks!.Any(check => check.Name == "full dotnet tests"));
            Assert.True(result.Checks.Any(check => check.ResultSummary == "covered by: full dotnet tests"));
        }
        finally
        {
            variables["MCG_ACCEPTANCE_CHANGE_SCOPED"] = previous;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_full_shards_override_runs_all_policy_shards")]
    public async Task GoalAcceptanceVerifierFullShardsOverrideRunsAllPolicyShards()
    {
        var variables = new Dictionary<string, string?>();
        using var policyScope = AcceptanceShardPolicySwitches.Use(new(ReadVariable: variables.GetValueOrDefault));
        var previous = variables.GetValueOrDefault("MCG_ACCEPTANCE_FULL_SHARDS");
        variables["MCG_ACCEPTANCE_FULL_SHARDS"] = "1";
        try
        {
            var root = CreateCheckedInManifestShapeWorkspace();
            var calls = new List<string[]>();
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Tests passed."
                        : ""));
            });

            var result = await verifier.RunAsync(
                root,
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"]);

            Assert.True(result.Passed);
            Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[1][2]);
            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
                .ToArray();
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, infrastructureCalls.Length);
            Assert.DoesNotContain(result.Checks!, check =>
                check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
        }
        finally
        {
            variables["MCG_ACCEPTANCE_FULL_SHARDS"] = previous;
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_dedupes_policy_injection_against_equivalent_manifest_check")]
    public async Task GoalAcceptanceVerifierDedupesPolicyInjectionAgainstEquivalentManifestCheck()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5.")    // core tests (manifest)
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        // Touching core source -> impact plan produces "core tests" (already in manifest).
        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        Assert.Single(calls);
        // "core tests" runs exactly once (not duplicated by injection)
        Assert.Equal(1, result.Checks!.Count(c => c.Name == "core tests"));
        Assert.Equal(0, result.Checks!.Count(c => c.Name == "infrastructure tests"));
        Assert.True(result.Checks.All(c => c.Passed));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_records_policy_alias_for_equivalent_manifest_command")]
    public async Task GoalAcceptanceVerifierRecordsPolicyAliasForEquivalentManifestCommand()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "renamed core coverage", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5."));
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        Assert.Single(calls);
        Assert.Equal(1, result.Checks!.Count(c => c.Name == "renamed core coverage"));
        var policyAlias = result.Checks.Single(c => c.Name == "core tests");
        Assert.True(policyAlias.Passed);
        Assert.Equal("covered by: renamed core coverage", policyAlias.ResultSummary);
        Assert.Equal(["renamed core coverage"], policyAlias.CoveredBy);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_failing_injected_policy_check_blocks_merge")]
    public async Task GoalAcceptanceVerifierFailingInjectedPolicyCheckBlocksMerge()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(1, "Failed! - Failed: 2, Passed: 3, Skipped: 0, Total: 5.")       // core tests (injected, fails)
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        // Core source file → impact plan requires "core tests", which gets injected and fails
        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Single(calls);
        // "core tests" ran (injected) and failed; "infrastructure tests" was not reached
        Assert.True(result.Checks!.Any(c => c.Name == "core tests" && !c.Passed));
        Assert.False(result.Checks.Any(c => c.Name == "infrastructure tests"));
    }

}
