using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsAdvisoryChecks : GoalAcceptanceVerifierDotnetBuildSlotTests, IDisposable
{
    private readonly List<string> runnerWorkspaces = [];

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
        var result = await verifier.RunAsync(CreateRunnerWorkspace());

        Assert.True(result.Passed);
        Assert.True(result.Checks is null || !result.Checks.Any(c => c.Advisory));
    }

    private string CreateAdvisoryWorkspace(string criteriaJson)
    {
        var root = CreateRunnerWorkspace();
        var orchestratorDir = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(orchestratorDir);
        File.WriteAllText(
            Path.Combine(orchestratorDir, "goal-acceptance-criteria.json"),
            criteriaJson);
        return root;
    }

    private string CreateRunnerWorkspace()
    {
        var root = CreateManifestWorkspace("""
            { "version": 1, "checks": [{ "name": "dotnet test", "type": "dotnet-test" }] }
            """);
        runnerWorkspaces.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in runnerWorkspaces)
            DeleteDirectoryWithRetry(root);
    }
}
