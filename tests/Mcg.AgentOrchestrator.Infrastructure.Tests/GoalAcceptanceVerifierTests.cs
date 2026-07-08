using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalAcceptanceVerifierTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_auto_runs_policy_required_browser_smoke")]
    public async Task GoalAcceptanceVerifierAutoRunsPolicyRequiredBrowserSmoke()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed."));
        });

        var result = await verifier.RunAsync(root, changedFiles: ["wwwroot/css/app.css"]);

        Assert.True(result.Passed);
        var browserCall = calls.Single(call => call.Contains(@".\scripts\Run-DashboardBrowserScript.ps1", StringComparer.OrdinalIgnoreCase));
        Assert.Equal("powershell", browserCall[0]);
        Assert.Contains(@".\scripts\dashboard-smoke.js", browserCall, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(result.Checks!, check => check.Name == "dashboard browser smoke" && check.Passed);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_blocks_forbidden_changed_paths_from_manifest")]
    public async Task GoalAcceptanceVerifierBlocksForbiddenChangedPathsFromManifest()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": [ "bin/**", ".scratch/**" ]
            }
            """);
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "src/ok.cs\nbin/Debug/generated.dll\n")
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
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--name-only", "main...HEAD"]));
        Assert.Equal(1, result.Checks!.Count);
        Assert.Equal("forbidden changed paths", result.Checks![0].Name);
        Assert.Contains("bin/Debug/generated.dll", result.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_classifies_transient_testhost_abort_vs_real_failure")]
    [Xunit.InlineData("The active Test Run was aborted because the host process exited unexpectedly.", true)]
    [Xunit.InlineData("Test Run Aborted.\r\n   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs", true)]
    [Xunit.InlineData("Failed!  - Failed:     1, Passed:  1012, Skipped:     0, Total:  1013", false)]
    [Xunit.InlineData("Failed:     1, Passed:  1012. The active Test Run was aborted because the host process exited unexpectedly.", false)]
    [Xunit.InlineData("Build FAILED.\r\nerror CS1002: ; expected", false)]
    [Xunit.InlineData("Passed!  - Failed:     0, Passed:  1013, Skipped:     0, Total:  1013", false)]
    public void ClassifiesTransientTesthostAbortVsRealFailure(string output, bool expected)
    {
        Assert.Equal(expected, GoalAcceptanceVerifier.IsTransientTesthostAbort(output));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_skips_dotnet_tests_for_docs_only_default_plan")]
    public async Task GoalAcceptanceVerifierSkipsDotnetTestsForDocsOnlyDefaultPlan()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, "")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree", changedFiles: ["README.md"]);

        Assert.True(result.Passed);
        Assert.Equal(1, calls.Count);
        Assert.True(calls[0].SequenceEqual(["dotnet", "build-server", "shutdown"]));
        var check = Xunit.Assert.Single(result.Checks!);
        Assert.Equal("test impact: no build required", check.Name);
        Xunit.Assert.Null(check.ExitCode);
        Xunit.Assert.Null(result.ArtifactsPath);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_uses_25_minute_default_timeout_when_unconfigured")]
    public async Task GoalAcceptanceVerifierUses25MinuteDefaultTimeoutWhenUnconfigured()
    {
        var previous = SetAcceptanceTimeoutEnvironment(null);
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<(string[] Args, TimeSpan Timeout)>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, timeout, _) =>
            {
                calls.Add((args, timeout));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            Assert.Equal(2, calls.Count);
            Assert.Equal(TimeSpan.FromMinutes(25), calls[0].Timeout);
            Assert.Equal(TimeSpan.FromMinutes(25), calls[1].Timeout);
        }
        finally
        {
            SetAcceptanceTimeoutEnvironment(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_manifest_timeout_override_wins_over_global_default")]
    public async Task GoalAcceptanceVerifierManifestTimeoutOverrideWinsOverGlobalDefault()
    {
        var previous = SetAcceptanceTimeoutEnvironment("7");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "slow custom check", "type": "command", "command": "custom-check", "arguments": ["--slow"], "timeoutMinutes": 2 }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<(string[] Args, TimeSpan Timeout)>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, timeout, _) =>
            {
                calls.Add((args, timeout));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            Assert.Equal(2, calls.Count);
            Assert.Equal(TimeSpan.FromMinutes(7), calls[0].Timeout);
            Assert.Equal(TimeSpan.FromMinutes(2), calls[1].Timeout);
            Assert.Equal("custom-check", calls[1].Args[0]);
        }
        finally
        {
            SetAcceptanceTimeoutEnvironment(previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_validates_state_effect_proposal_schema")]
    public async Task GoalAcceptanceVerifierValidatesStateEffectProposalSchema()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "fast no-op", "type": "no-op" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var proposalDirectory = Path.Combine(root, ".orchestrator-proposals");
            Directory.CreateDirectory(proposalDirectory);
            File.WriteAllText(Path.Combine(proposalDirectory, "backlog-add-missing-title.md"), """
                ---
                kind: backlog-add
                ---
                Body only is not enough.
                """);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
                Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty)));

            var result = await verifier.RunAsync(
                root,
                changedFiles: [".orchestrator-proposals/backlog-add-missing-title.md"]);

            Assert.False(result.Passed);
            var check = Assert.Single(result.Checks!.Where(check => check.Name == "state-effect proposal schema"));
            Assert.False(check.Passed);
            Assert.Contains("missing required field 'title'", check.OutputTail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public abstract class GoalAcceptanceVerifierTestBase
{
    protected static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(Path.Combine(manifestDirectory, "acceptance-manifest.json"), manifest);
        return root;
    }

    protected static string? SetAcceptanceTimeoutEnvironment(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable);
        Environment.SetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable, value);
        return previous;
    }
}

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTests : GoalAcceptanceVerifierTestBase
{
    [OptInRealAcceptanceVerifierFact(DisplayName = "GoalAcceptanceVerifier_real_runner_smoke_is_opt_in")]
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
            Directory.Delete(root, recursive: true);
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
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partitions_checked_in_infrastructure_manifest_check")]
    public async Task GoalAcceptanceVerifierPartitionsCheckedInInfrastructureManifestCheck()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });

            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            var checks = result.Checks ?? throw new InvalidOperationException("Expected acceptance checks.");
            Assert.True(checks.Any(check => check.Name == "infrastructure tests: Cli"));
            Assert.True(checks.Any(check => check.Name == "infrastructure tests: Goal acceptance verifier"));
            Assert.True(checks.Any(check => check.Name == "infrastructure tests: Remainder"));
            Assert.False(checks.Any(check => check.Name == "infrastructure tests"));

            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2].EndsWith("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.Ordinal))
                .ToList();

            Assert.DoesNotContain(infrastructureCalls, call => !call.Contains("--filter"));
            Assert.Contains(infrastructureCalls, call => call.Contains("FullyQualifiedName~CliCommandTests"));
            Assert.Contains(infrastructureCalls, call => call.Contains("FullyQualifiedName~GoalAcceptanceVerifierTests"));
            Assert.Contains(infrastructureCalls, call => call.Contains("FullyQualifiedName~DashboardHostTests&Category!=HostIntegration"));
            Assert.Contains(infrastructureCalls, call =>
                call.Any(argument =>
                    argument.Contains("FullyQualifiedName!~GoalAcceptanceVerifierTests", StringComparison.Ordinal) &&
                    argument.Contains("FullyQualifiedName!~DashboardRenderingTests", StringComparison.Ordinal) &&
                    argument.Contains("Category!=HostIntegration", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_self_heals_once_on_CS2012_file_lock_and_returns_passed")]
    public async Task GoalAcceptanceVerifierSelfHealsOnceOnCs2012FileLockAndReturnsPassed()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                                                 // build-server shutdown
            new(1, "error CS2012: Cannot open 'Core.dll' for writing because it is being used by another process."),
            new(0, ""),                                                                                 // build-server shutdown (retry)
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")                              // dotnet test - retry passes
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var goalId = new GoalId("abcd1234abcd1234abcd1234abcd1234");
        var result = await verifier.RunAsync("C:\\fake\\worktree", goalId);

        Assert.True(result.Passed);
        Assert.True(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(4, calls.Count);
        Assert.True(calls[2].SequenceEqual(["dotnet", "build-server", "shutdown"]));
        AssertIsolatedTestCommand(calls[1]);
        AssertIsolatedTestCommand(calls[3]);
        Assert.Equal(GetArtifactsPath(calls[1]), GetArtifactsPath(calls[3]));
        Assert.True(result.ArtifactsPath is not null);
        Assert.Contains(Path.Combine("slots", "slot-"), result.ArtifactsPath!, StringComparison.OrdinalIgnoreCase);
        var check = result.Checks!.Single(item => item.Name == "dotnet test");
        Assert.Equal("goal-acceptance-verifier", check.BrokerName);
        Assert.Equal("goal-abcd1234", check.LeaseId);
        Assert.True(check.DurationMilliseconds is >= 0);
        Assert.True(check.LockRemediationApplied);
        Assert.True(check.ResultSummary?.Contains("transient compiler lock detected; build server restarted; check retried", StringComparison.Ordinal) == true);
        Assert.True(check.ResultSummary?.Contains("Failed: 0", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_self_heals_once_on_compiler_lock_and_returns_failed_when_retry_also_fails")]
    public async Task GoalAcceptanceVerifierSelfHealsOnceOnCompilerLockAndReturnsFailedWhenRetryAlsoFails()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "MSB3491: Could not write lines to file because it is being used by another process."),
            new(0, ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing because it is being used by another process.")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.False(result.Passed);
        Assert.True(result.Retried);
        Assert.True(result.OutputTail is not null);
        Assert.True(result.OutputTail!.Contains("CS2012", StringComparison.Ordinal));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(4, calls.Count);
        AssertIsolatedTestCommand(calls[1]);
        AssertIsolatedTestCommand(calls[3]);
        var check = result.Checks!.Single(item => item.Name == "dotnet test");
        Assert.True(check.LockRemediationApplied);
        Assert.Equal("transient compiler lock detected; build server restarted; check retried", check.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_does_not_self_heal_non_lock_failure")]
    public async Task GoalAcceptanceVerifierDoesNotSelfHealNonLockFailure()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.False(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is not null);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(2, calls.Count);
        AssertIsolatedTestCommand(calls[1]);
        Assert.False(result.Checks!.Single(item => item.Name == "dotnet test").LockRemediationApplied);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_returns_failed_check_with_timeout_diagnostics")]
    public async Task GoalAcceptanceVerifierReturnsFailedCheckWithTimeoutDiagnostics()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "focused CLI infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": [ "--filter", "CliHelpTests", "--verbosity", "minimal" ] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var calls = new List<string[]>();
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(calls.Count == 1
                    ? new GoalAcceptanceVerifier.CommandResult(0, "")
                    : new GoalAcceptanceVerifier.CommandResult(
                        -1,
                        "restore complete\nstill running infrastructure tests",
                        TimedOut: true,
                        CommandLine: "dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                        StdoutPath: "C:\\temp\\acceptance.out",
                        StderrPath: "C:\\temp\\acceptance.err",
                        Timeout: TimeSpan.FromMinutes(10)));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("abcd1234abcd1234abcd1234abcd1234"));

            Assert.False(result.Passed);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(2, calls.Count);
            var check = Assert.Single(result.Checks!.Where(check => !check.Passed));
            Assert.Equal("acceptance-check-timeout: focused-cli-infrastructure-tests elapsed=10m budget=10m", check.Name);
            Assert.False(check.Passed);
            Assert.Equal("elapsed=10m budget=10m", check.ResultSummary);
            Assert.Equal(result.ArtifactsPath, check.ArtifactsPath);
            var outputTail = result.OutputTail ?? string.Empty;
            Assert.True(outputTail.Contains("Verification command timed out after elapsed=10m budget=10m.", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("Command: dotnet test", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("stdout: C:\\temp\\acceptance.out", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("stderr: C:\\temp\\acceptance.err", StringComparison.Ordinal), outputTail);
            Assert.True(outputTail.Contains("still running infrastructure tests", StringComparison.Ordinal), outputTail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_returns_passed_without_retry_on_first_time_pass")]
    public async Task GoalAcceptanceVerifierReturnsPassedWithoutRetryOnFirstTimePass()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Test run succeeded.")
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.True(result.Passed);
        Assert.False(result.Retried);
        Assert.True(result.OutputTail is null);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, calls.Count);
        AssertIsolatedTestCommand(calls[1]);
        Assert.Equal(1, result.Checks!.Count);
        Assert.Equal("dotnet test", result.Checks![0].Name);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_selects_infrastructure_tests_from_default_plan")]
    public async Task GoalAcceptanceVerifierSelectsInfrastructureTestsFromDefaultPlan()
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

        var result = await verifier.RunAsync(
            "C:\\fake\\worktree",
            changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"]);

        Assert.True(result.Passed);
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        Assert.Equal(18, infrastructureCalls.Length);
        Assert.DoesNotContain(calls, call => call.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparer.Ordinal));
        foreach (var call in infrastructureCalls)
        {
            AssertIsolatedTestCommand(call);
            Assert.Contains("--filter", call);
        }

        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Worker profiles");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == "covered by 18 partitioned checks");
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_runs_focused_infrastructure_filter_for_cli_only_changes")]
    public async Task GoalAcceptanceVerifierRunsFocusedInfrastructureFilterForCliOnlyChanges()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ""),
            new(0, "Focused CLI tests passed.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(3, calls.Count);
        Assert.Equal("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", calls[2][2]);
        Assert.DoesNotContain(calls[2], argument => argument.Contains("Mcg.AgentOrchestrator.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--filter", calls[2]);
        Assert.Contains(calls[2], argument => argument.Contains("CliHelpTests", StringComparison.Ordinal));
        Assert.False(calls[2].Any(argument => argument.Contains("FundamentalAliasTests", StringComparison.Ordinal)));
        Assert.False(calls[2].Any(argument => argument.Contains("CliCommandTests", StringComparison.Ordinal)));
        Assert.DoesNotContain(calls[2], argument => argument.Contains("DashboardHostTests", StringComparison.Ordinal));
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
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ""),
            new(0, "Focused CLI tests passed."),
            new(0, ""),
            new(0, "")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(5, calls.Count);
        Assert.DoesNotContain(calls, call => call.Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparer.OrdinalIgnoreCase));
        Assert.Equal("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", calls[2][2]);
        Assert.Contains("--filter", calls[2]);
        Assert.Contains(calls[2], argument => argument.Contains("WorkerProfileTests", StringComparison.Ordinal));
        Assert.DoesNotContain(
            calls,
            call => call.Length > 2 &&
                call[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                call[1].Equals("test", StringComparison.OrdinalIgnoreCase) &&
                call[2].Equals("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase) &&
                !call.Contains("--filter", StringComparer.OrdinalIgnoreCase));
        var coreReceipt = result.Checks!.Single(check => check.Name == "core tests");
        Assert.True(coreReceipt.Passed);
        Assert.Contains("skipped: no changed file in dependency closure", coreReceipt.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("changed projects: Infrastructure.Tests", coreReceipt.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("dependency closure: Infrastructure.Tests", coreReceipt.ResultSummary, StringComparison.Ordinal);
        var infrastructureReceipt = result.Checks.Single(check => check.Name == "infrastructure tests");
        Assert.Contains("covered by: focused changed infrastructure tests", infrastructureReceipt.ResultSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partitions_infrastructure_suite_for_multiple_app_subsystems")]
    public async Task GoalAcceptanceVerifierPartitionsInfrastructureSuiteForMultipleAppSubsystems()
    {
        var root = CreateStandardManifestWorkspace();
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

        var result = await verifier.RunAsync(
            root,
            changedFiles:
            [
                "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Tasks.cs",
                "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"
            ]);

        Assert.True(result.Passed);
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        Assert.Equal(18, infrastructureCalls.Length);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        foreach (var call in infrastructureCalls)
            Assert.Contains("--filter", call);
        Assert.Contains(infrastructureCalls, call => call.Contains("FullyQualifiedName~CliCommandTests"));
        Assert.Contains(infrastructureCalls, call => call.Contains("FullyQualifiedName~DashboardRenderingTests"));
        Assert.Contains(infrastructureCalls, call =>
            call.Any(argument => argument.Contains("FullyQualifiedName!~DashboardHostTests", StringComparison.Ordinal) &&
                argument.Contains("Category!=HostIntegration", StringComparison.Ordinal)));
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Cli");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Dashboard rendering");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == "covered by 18 partitioned checks");
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
            Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
                .ToArray();
            Assert.Equal(18, infrastructureCalls.Length);
            foreach (var call in infrastructureCalls)
                Assert.Contains("--filter", call);
            Assert.DoesNotContain(infrastructureCalls, call => !call.Contains("--filter"));
            Assert.DoesNotContain(result.Checks!, check =>
                check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
            Assert.Contains(infrastructureCalls, call =>
                call.Any(argument => argument.Contains("FullyQualifiedName!~DashboardHostTests", StringComparison.Ordinal) &&
                    argument.Contains("Category!=HostIntegration", StringComparison.Ordinal)));
            Assert.Contains(result.Checks!, check =>
                check.Name == "infrastructure tests" &&
                check.Passed &&
                check.ResultSummary == "covered by 18 partitioned checks");
            Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        }

        await AssertFullShardRunAsync(
            root,
            [
                "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"
            ]);
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
        Assert.Equal(3, calls.Count);
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--check"]));
        Assert.Equal("dotnet", calls[2][0]);
        Assert.Equal("test", calls[2][1]);
        Assert.Equal("tests/Example.Tests.csproj", calls[2][2]);
        Assert.True(calls[2].Contains("--filter", StringComparer.Ordinal));
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
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root, new GoalId("13572468135724681357246813572468"));

        Assert.True(result.Passed);
        Assert.Equal(2, calls.Count);
        Assert.Equal("dotnet", calls[1][0]);
        Assert.Equal("test", calls[1][1]);
        AssertIsolatedTestCommand(calls[1]);
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
        Assert.Equal(3, calls.Count);
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--check"]));
        AssertIsolatedTestCommand(calls[2]);
        Assert.False(calls[2].Any(a => a.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(4, result.Checks!.Count);

        var fullCheck = result.Checks.Single(c => c.Name == "full dotnet tests");
        Assert.True(fullCheck.Passed);
        Assert.True(fullCheck.ResultSummary?.Contains("Passed:", StringComparison.Ordinal) == true);

        var coreCheck = result.Checks.Single(c => c.Name == "core tests");
        Assert.True(coreCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", coreCheck.ResultSummary);
        Assert.Equal(fullCheck.ArtifactsPath, coreCheck.ArtifactsPath);
        Assert.Equal(fullCheck.LeaseId, coreCheck.LeaseId);

        var infraCheck = result.Checks.Single(c => c.Name == "infrastructure tests");
        Assert.True(infraCheck.Passed);
        Assert.Equal("covered by: full dotnet tests", infraCheck.ResultSummary);
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
        Assert.Equal(3, calls.Count);
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
            new(0, ""),
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
        // shutdown + sln run + extra tests (not in solution)
        Assert.Equal(3, calls.Count);
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
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                        // build-server shutdown
            new(0, ""),                                        // git diff --check
            new(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5.")  // core tests
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        // Core source file -> impact plan requires only "core tests".
        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Core/Application/Foo.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(3, calls.Count);
        Assert.True(calls[1].SequenceEqual(["git", "diff", "--check"]));
        Assert.Equal("dotnet", calls[2][0]);
        Assert.Equal("test", calls[2][1]);
        Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
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
        Assert.Equal(22, calls.Count);
        Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        Assert.Equal(18, infrastructureCalls.Length);
        foreach (var call in infrastructureCalls)
            Assert.Contains("--filter", call);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        Assert.Contains(result.Checks!, check => check.Name == "core tests");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == "covered by 18 partitioned checks");
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_substitutes_solution_check_with_partitioned_infrastructure_checks_for_infra_scope")]
    public async Task GoalAcceptanceVerifierSubstitutesSolutionCheckWithPartitionedInfrastructureChecksForInfraScope()
    {
        var root = CreateStandardManifestWorkspace();
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

        var result = await verifier.RunAsync(
            root,
            changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"]);

        Assert.True(result.Passed);
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        Assert.Equal(18, infrastructureCalls.Length);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        foreach (var call in infrastructureCalls)
            Assert.Contains("--filter", call);
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Goal acceptance verifier");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == "covered by 18 partitioned checks");
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_substitutes_solution_check_with_union_for_core_and_infra_scope")]
    public async Task GoalAcceptanceVerifierSubstitutesSolutionCheckWithUnionForCoreAndInfraScope()
    {
        var root = CreateStandardManifestWorkspace();
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
            changedFiles:
            [
                "src/Mcg.AgentOrchestrator.Core/Application/Foo.cs",
                "src/Mcg.AgentOrchestrator.Infrastructure/Workers/Foo.cs"
            ]);

        Assert.True(result.Passed);
        Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
        Assert.DoesNotContain(calls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        Assert.Equal(18, infrastructureCalls.Length);
        foreach (var call in infrastructureCalls)
            Assert.Contains("--filter", call);
        Assert.Contains(result.Checks!, check => check.Name == "core tests");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Cli");
        Assert.Contains(result.Checks!, check => check.Name == "infrastructure tests: Remainder");
        Assert.Contains(result.Checks!, check =>
            check.Name == "infrastructure tests" &&
            check.Passed &&
            check.ResultSummary == "covered by 18 partitioned checks");
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_keeps_solution_check_for_build_security_broad_or_disabled_scopes")]
    public async Task GoalAcceptanceVerifierKeepsSolutionCheckForBuildSecurityBroadOrDisabledScopes()
    {
        var root = CreateStandardManifestWorkspace();
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
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
        Assert.Contains("Mcg.AgentOrchestrator.sln", calls[2], StringComparer.OrdinalIgnoreCase);
        Assert.True(result.Checks!.Any(check => check.Name == "full dotnet tests"));
        Assert.True(result.Checks.Any(check => check.ResultSummary == "covered by: full dotnet tests"));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_change_scoped_kill_switch_keeps_solution_check")]
    public async Task GoalAcceptanceVerifierChangeScopedKillSwitchKeepsSolutionCheck()
    {
        var previous = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", "0");
        try
        {
            var root = CreateStandardManifestWorkspace();
            var calls = new List<string[]>();
            var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
                new(0, ""),
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
            Assert.Contains("Mcg.AgentOrchestrator.sln", calls[2], StringComparer.OrdinalIgnoreCase);
            Assert.True(result.Checks!.Any(check => check.Name == "full dotnet tests"));
            Assert.True(result.Checks.Any(check => check.ResultSummary == "covered by: full dotnet tests"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED", previous);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_full_shards_override_runs_all_policy_shards")]
    public async Task GoalAcceptanceVerifierFullShardsOverrideRunsAllPolicyShards()
    {
        var previous = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", "1");
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
            Assert.Equal("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", calls[2][2]);
            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
                .ToArray();
            Assert.Equal(18, infrastructureCalls.Length);
            Assert.DoesNotContain(result.Checks!, check =>
                check.ResultSummary?.Contains("skipped: no changed file in dependency closure", StringComparison.Ordinal) == true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS", previous);
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
            new(0, ""),                                                        // build-server shutdown
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
        Assert.Equal(2, calls.Count);
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
        Assert.Equal(2, calls.Count);
        Assert.Equal(1, result.Checks!.Count(c => c.Name == "renamed core coverage"));
        var policyAlias = result.Checks.Single(c => c.Name == "core tests");
        Assert.True(policyAlias.Passed);
        Assert.Equal("covered by: renamed core coverage", policyAlias.ResultSummary);
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
            new(0, ""),                                                            // build-server shutdown
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
        Assert.Equal(2, calls.Count);
        // "core tests" ran (injected) and failed; "infrastructure tests" was not reached
        Assert.True(result.Checks!.Any(c => c.Name == "core tests" && !c.Passed));
        Assert.False(result.Checks.Any(c => c.Name == "infrastructure tests"));
    }

    private static void AssertIsolatedTestCommand(string[] args)
    {
        Assert.Equal("dotnet", args[0]);
        Assert.Equal("test", args[1]);
        Assert.True(args.Any(arg => arg.Equals("--artifacts-path", StringComparison.Ordinal)));
        Assert.False(args.Any(arg => arg.Equals("--disable-build-servers", StringComparison.Ordinal)));
        Assert.False(args.Any(arg => arg.Equals("-p:UseSharedCompilation=false", StringComparison.Ordinal)));
        Assert.True(args.Any(arg => arg.StartsWith("-maxcpucount:", StringComparison.Ordinal) && !arg.Equals("-maxcpucount:1", StringComparison.Ordinal)));
        Assert.Contains("mcg-dotnet-isolated", GetArtifactsPath(args), StringComparison.Ordinal);
    }

    private static string GetArtifactsPath(string[] args)
    {
        var artifactsPathIndex = Array.IndexOf(args, "--artifacts-path");
        Assert.True(artifactsPathIndex >= 0);
        Assert.True(artifactsPathIndex + 1 < args.Length);
        return args[artifactsPathIndex + 1];
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_grep_absent_failure_does_not_affect_passed")]
    public async Task GoalAcceptanceVerifierAdvisoryGrepAbsentFailureDoesNotAffectPassed()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "grep confirms no path still calls `new JsonSerializerOptions`",
                "type": "grep-absent",
                "pattern": "new JsonSerializerOptions"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                         // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5."),   // dotnet test
            new(0, "src/Foo.cs:JsonSerializerOptions opts = new JsonSerializerOptions();") // git grep (pattern found)
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        // Suite passed, advisory grep-absent FAILED (pattern found)
        Assert.True(result.Passed);
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.False(advisoryCheck.Passed);
        Assert.True(advisoryCheck.Advisory);
        Assert.True(advisoryCheck.Name.Contains("new JsonSerializerOptions", StringComparison.Ordinal));
        // grep was the last call
        Assert.Equal("git", calls.Last()[0]);
        Assert.Equal("grep", calls.Last()[1]);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_grep_absent_passes_when_pattern_absent")]
    public async Task GoalAcceptanceVerifierAdvisoryGrepAbsentPassesWhenPatternAbsent()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "grep confirms no `OldClass` remains",
                "type": "grep-absent",
                "pattern": "OldClass"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),   // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2."), // dotnet test
            new(1, "")    // git grep exit 1 = pattern not found
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.True(advisoryCheck.Passed);
        Assert.Equal("pattern absent", advisoryCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_file_exists_check_reports_pass_and_fail")]
    public async Task GoalAcceptanceVerifierAdvisoryFileExistsCheckReportsPassAndFail()
    {
        // Use a file that does NOT yet exist in the workspace
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "`.orchestrator/output.txt` is produced",
                "type": "file-exists",
                "path": ".orchestrator/output.txt"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),  // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")  // dotnet test
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        // First run: .orchestrator/output.txt does not exist
        var resultMissing = await verifier.RunAsync(root);
        Assert.True(resultMissing.Passed);
        var missingCheck = resultMissing.Checks!.Single(c => c.Advisory);
        Assert.False(missingCheck.Passed);
        Assert.Equal("file not found", missingCheck.ResultSummary);

        // Create the file and verify it now passes
        var responses2 = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")
        ]);
        File.WriteAllText(Path.Combine(root, ".orchestrator", "output.txt"), "done");

        var verifier2 = new GoalAcceptanceVerifier((args, _, _) =>
        {
            return Task.FromResult(responses2.Dequeue());
        });

        var resultPresent = await verifier2.RunAsync(root);
        Assert.True(resultPresent.Passed);
        var presentCheck = resultPresent.Checks!.Single(c => c.Advisory);
        Assert.True(presentCheck.Passed);
        Assert.Equal("file exists", presentCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_command_exit_runs_and_reports")]
    public async Task GoalAcceptanceVerifierAdvisoryCommandExitRunsAndReports()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "`dotnet build Fake.sln -c Release` clean, no new warnings.",
                "type": "command-exit",
                "command": "dotnet build Fake.sln -c Release"
              }
            ]
            """);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),   // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."),   // dotnet test
            new(0, "Build succeeded.")  // dotnet build (advisory command-exit)
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        Assert.True(result.Passed);
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.True(advisoryCheck.Passed);
        Assert.True(advisoryCheck.Advisory);

        // Advisory command was the last call
        var lastCall = calls.Last();
        Assert.Equal("dotnet", lastCall[0]);
        Assert.Equal("build", lastCall[1]);
        Assert.Equal("Fake.sln", lastCall[2]);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_advisory_checks_run_even_when_suite_fails")]
    public async Task GoalAcceptanceVerifierAdvisoryChecksRunEvenWhenSuiteFails()
    {
        var root = CreateAdvisoryWorkspace("""
            [
              {
                "name": "`.orchestrator/marker.txt` exists",
                "type": "file-exists",
                "path": ".orchestrator/marker.txt"
              }
            ]
            """);
        File.WriteAllText(Path.Combine(root, ".orchestrator", "marker.txt"), "exists");

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),   // build-server shutdown
            new(1, "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1.")  // dotnet test FAILS
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(root);

        // Overall failed because suite failed
        Assert.False(result.Passed);
        // But advisory check still ran and passed
        var advisoryCheck = result.Checks!.Single(c => c.Advisory);
        Assert.True(advisoryCheck.Passed);
        Assert.Equal("file exists", advisoryCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_loads_no_advisory_checks_when_criteria_file_missing")]
    public async Task GoalAcceptanceVerifierLoadsNoAdvisoryChecksWhenCriteriaFileMissing()
    {
        var calls = new List<string[]>();

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                    ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                    : ""));
        });

        // Use a plain worktree with no criteria file
        var result = await verifier.RunAsync("C:\\fake\\worktree");

        Assert.True(result.Passed);
        Assert.True(result.Checks is null || !result.Checks.Any(c => c.Advisory));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_deleted_fact_method")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsDeletedFactMethod()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,5 +10,0 @@",
            "-    [Xunit.Fact(DisplayName = \"some test\")]",
            "-    public void SomeTest()",
            "-    {",
            "-        Assert.True(something);",
            "-    }"
        ]);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                              // build-server shutdown
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."),        // dotnet test
            new(0, diff)                                                             // git diff test tamper
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(
            "C:\\fake\\worktree",
            changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        // Suite still passed — advisory does not gate
        Assert.True(result.Passed);
        Assert.Equal(0, result.ExitCode);

        // Tamper check is advisory and failed
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.True(tamperCheck.OutputTail is not null);
        Assert.Contains("FooTests.cs", tamperCheck.OutputTail!, StringComparison.Ordinal);

        // Git diff was the last call
        var lastCall = calls.Last();
        Assert.Equal("git", lastCall[0]);
        Assert.Equal("diff", lastCall[1]);
        Assert.True(lastCall.Any(a => a.Equals("main...HEAD", StringComparison.Ordinal)));
        Assert.True(lastCall.Any(a => a.Equals("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_passes_when_assertions_added")]
    public async Task GoalAcceptanceVerifierTestTamperGuardPassesWhenAssertionsAdded()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -10,0 +10,5 @@",
            "+    [Xunit.Fact(DisplayName = \"new test\")]",
            "+    public void NewTest()",
            "+    {",
            "+        Assert.True(something);",
            "+    }"
        ]);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."),
            new(0, diff)
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(
            "C:\\fake\\worktree",
            changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.True(tamperCheck.Passed);
        Assert.Equal("no test degradation detected", tamperCheck.ResultSummary);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_flags_tautology_assertion")]
    public async Task GoalAcceptanceVerifierTestTamperGuardFlagsTautologyAssertion()
    {
        var diff = string.Join("\n", [
            "diff --git a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "--- a/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "+++ b/tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs",
            "@@ -12,1 +12,1 @@",
            "-        Assert.True(something);",
            "+        Assert.True(true);"
        ]);

        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."),
            new(0, diff)
        ]);

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(
            "C:\\fake\\worktree",
            changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs"]);

        Assert.True(result.Passed);
        var tamperCheck = result.Checks!.Single(c => c.Name == "test tamper guard");
        Assert.True(tamperCheck.Advisory);
        Assert.False(tamperCheck.Passed);
        Assert.True(tamperCheck.OutputTail is not null);
        Assert.Contains("tautology", tamperCheck.OutputTail!, StringComparison.OrdinalIgnoreCase);
        var tautologyAssertion = "Assert." + "True(true)";
        Assert.Contains(tautologyAssertion, tamperCheck.OutputTail!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_test_tamper_guard_absent_when_no_test_files_in_diff")]
    public async Task GoalAcceptanceVerifierTestTamperGuardAbsentWhenNoTestFilesInDiff()
    {
        var calls = new List<string[]>();

        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                0,
                args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                    ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                    : ""));
        });

        var result = await verifier.RunAsync(
            "C:\\fake\\worktree",
            changedFiles: [
                "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs",
                "README.md"
            ]);

        Assert.True(result.Passed);
        Assert.False(result.Checks?.Any(c => c.Name == "test tamper guard") == true);
        var infrastructureCalls = calls
            .Where(call => call.Length > 2 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")
            .ToArray();
        Assert.Equal(18, infrastructureCalls.Length);
        foreach (var call in infrastructureCalls)
            Assert.Contains("--filter", call);
        Assert.Equal(19, calls.Count); // shutdown + partitioned infra tests; no git diff call
    }

    private static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(Path.Combine(manifestDirectory, "acceptance-manifest.json"), manifest);
        return root;
    }

    private static string? SetAcceptanceTimeoutEnvironment(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable);
        Environment.SetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable, value);
        return previous;
    }

    private static string CreateStandardManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "full dotnet tests", "type": "dotnet-test", "project": "Mcg.AgentOrchestrator.sln", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    private static string CreateCheckedInManifestShapeWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": [
                "bin/**",
                "obj/**",
                ".scratch/**",
                ".orchestrator-prototype/**",
                "TestResults/**",
                "playwright-report/**"
              ]
            }
            """);

    private static string CreateAdvisoryWorkspace(string criteriaJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var orchestratorDir = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(orchestratorDir);
        File.WriteAllText(
            Path.Combine(orchestratorDir, "goal-acceptance-criteria.json"),
            criteriaJson);
        return root;
    }
}

internal sealed class OptInRealAcceptanceVerifierFactAttribute : Xunit.FactAttribute
{
    private const string EnvironmentVariable = "MCG_RUN_REAL_ACCEPTANCE_VERIFIER_TESTS";

    public OptInRealAcceptanceVerifierFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {EnvironmentVariable}=1 to run the opt-in real acceptance verifier smoke.";
        }
    }
}
