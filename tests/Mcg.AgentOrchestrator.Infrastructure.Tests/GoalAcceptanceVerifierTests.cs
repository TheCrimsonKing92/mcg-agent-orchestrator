using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalAcceptanceVerifierTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_retries_once_on_CS2012_and_returns_passed")]
    public async Task GoalAcceptanceVerifierRetriesOnceOnCs2012AndReturnsPassed()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),                                                     // build-server shutdown
            new(1, "error CS2012: Cannot open 'Core.dll' for writing"),     // dotnet test - CS2012
            new(0, ""),                                                     // build-server shutdown (retry)
            new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.")  // dotnet test - retry passes
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
        Assert.Contains(result.ArtifactsPath!, text => text.Contains(Path.Combine("goals", "abcd1234"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ArtifactsPath!, text => text.Contains(Path.Combine("lease", "artifacts"), StringComparison.OrdinalIgnoreCase));
        var check = result.Checks!.Single(item => item.Name == "dotnet test");
        Assert.Equal("goal-acceptance-verifier", check.BrokerName);
        Assert.Equal("goal-abcd1234", check.LeaseId);
        Assert.True(check.DurationMilliseconds is >= 0);
        Assert.True(check.LockRemediationApplied);
        Assert.True(check.ResultSummary?.Contains("Failed: 0", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_retries_once_on_CS2012_and_returns_failed_when_retry_also_fails")]
    public async Task GoalAcceptanceVerifierRetriesOnceOnCs2012AndReturnsFailedWhenRetryAlsoFails()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing"),
            new(0, ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing -- still locked")
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
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_does_not_retry_non_CS2012_failure")]
    public async Task GoalAcceptanceVerifierDoesNotRetryNonCs2012Failure()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(1, "Failed 3 tests.\nError: assertion failed")
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

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_selects_infrastructure_tests_from_default_plan")]
    public async Task GoalAcceptanceVerifierSelectsInfrastructureTestsFromDefaultPlan()
    {
        var calls = new List<string[]>();
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, "Infrastructure tests passed.")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        var result = await verifier.RunAsync(
            "C:\\fake\\worktree",
            changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"]);

        Assert.True(result.Passed);
        Assert.Equal(2, calls.Count);
        Assert.Equal("dotnet", calls[1][0]);
        Assert.Equal("test", calls[1][1]);
        Assert.Equal("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", calls[1][2]);
        Assert.False(calls[1].Contains("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", StringComparer.Ordinal));
        AssertIsolatedTestCommand(calls[1]);
        var check = Xunit.Assert.Single(result.Checks!);
        Assert.Equal("infrastructure tests", check.Name);
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
        Assert.Contains(result.OutputTail!, text => text.Contains("bin/Debug/generated.dll", StringComparison.Ordinal));
    }

    private static void AssertIsolatedTestCommand(string[] args)
    {
        Assert.Equal("dotnet", args[0]);
        Assert.Equal("test", args[1]);
        Assert.True(args.Any(arg => arg.Equals("--artifacts-path", StringComparison.Ordinal)));
        Assert.True(args.Any(arg => arg.Equals("-p:UseSharedCompilation=false", StringComparison.Ordinal)));
        Assert.Contains(GetArtifactsPath(args), text => text.Contains("mcg-dotnet-isolated", StringComparison.Ordinal));
    }

    private static string GetArtifactsPath(string[] args)
    {
        var artifactsPathIndex = Array.IndexOf(args, "--artifacts-path");
        Assert.True(artifactsPathIndex >= 0);
        Assert.True(artifactsPathIndex + 1 < args.Length);
        return args[artifactsPathIndex + 1];
    }

    private static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests", Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(Path.Combine(manifestDirectory, "acceptance-manifest.json"), manifest);
        return root;
    }
}
