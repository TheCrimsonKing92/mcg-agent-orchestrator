using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class TrialCompareCliCommandTests
{
    [Xunit.Fact]
    public async Task HistoricalTrialCompareThroughProgramLoadsPersistedGoal()
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var goal = HistoricalGoal(Dispatch(0, "brief one", "base-sha", "OpenAI", "gpt-a")) with
            {
                Objective = "brief one",
                BriefVersions =
                [
                    new GoalBriefVersion(1, "brief one", DateTimeOffset.Parse("2026-01-01T00:00:00Z"))
                ]
            };
            await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([goal], [])));
            var specPath = WriteHistoricalSpec(root, goal.Id);
            var before = JsonSerializer.Serialize((await repository.LoadAsync()).ExportSnapshot());

            Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["trial-compare", "--spec", specPath]));

            var result = RunAppCli(root, "trial-compare", "--spec", specPath);
            var after = JsonSerializer.Serialize((await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync()).ExportSnapshot());

            Xunit.Assert.Equal(1, result.ExitCode);
            Xunit.Assert.Contains("At least two harnesses are required", result.StandardError, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain(nameof(TrialComparisonUnavailableReason.HistoricalGoalNotFound), result.StandardOutput, StringComparison.Ordinal);
            Xunit.Assert.Equal(before, after);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void HistoricalTrialCompareThroughProgramReportsActuallyMissingGoal()
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var specPath = WriteHistoricalSpec(root, "missing-goal");

            var result = RunAppCli(root, "trial-compare", "--spec", specPath);

            Xunit.Assert.Equal(1, result.ExitCode);
            Xunit.Assert.Contains(nameof(TrialComparisonUnavailableReason.HistoricalGoalNotFound), result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void ParseSpecRequiresCanonicalWorkloadEvidence()
    {
        var exception = Xunit.Assert.Throws<TrialComparisonUnavailableException>(() =>
            TrialCompareCliCommand.ParseSpec(
                """{"sourceRepositoryPath":"repo","baseCommit":"abc","harnesses":[]}""",
                ["trial-compare"],
                "receipts"));

        Xunit.Assert.Equal(TrialComparisonUnavailableReason.InvalidExplicitWorkload, exception.Reason);
    }

    [Xunit.Fact]
    public void ParseSpecAcceptsNamedHarnessesAndOperatorOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var receipts = Path.Combine(root, "receipts");
            const string brief = "same historical brief\r\nwithout normalization";
            var digest = TrialIdentity.ComputeBriefDigest(brief);
            var json = $$"""
                {
                  "sourceRepositoryPath": "{{root.Replace("\\", "\\\\")}}",
                  "baseCommit": "abc123",
                  "workload": {
                    "briefIdentity": "brief-v3",
                    "briefContent": "same historical brief\r\nwithout normalization",
                    "briefDigest": "{{digest}}",
                    "modelIdentity": "OpenAI/gpt-test",
                    "sourceProvenance": "explicit:test"
                  },
                  "protectedPaths": ["protected.txt"],
                  "harnesses": [
                    { "name": "alpha", "fileName": "alpha.exe", "arguments": ["one"], "environment": { "MODE": "a" } },
                    { "name": "beta", "fileName": "beta.exe", "arguments": ["two"], "environment": { "MODE": "b" } }
                  ]
                }
                """;

            var request = TrialCompareCliCommand.ParseSpec(
                json,
                ["trial-compare", "--receipts", receipts, "--timeout-seconds", "45"],
                Path.Combine(root, "default-receipts"));

            Xunit.Assert.Equal("abc123", request.BaseCommit);
            Xunit.Assert.Equal(brief, request.Workload.BriefContent);
            Xunit.Assert.Equal(digest, request.Workload.BriefDigest);
            Xunit.Assert.Equal(2, request.Harnesses.Count);
            Xunit.Assert.Equal(Path.GetFullPath(receipts), request.ReceiptsDirectory);
            Xunit.Assert.Equal(TimeSpan.FromSeconds(45), request.LaunchTimeout);
            Xunit.Assert.Equal("a", request.Harnesses[0].Environment!["MODE"]);
            Xunit.Assert.Equal("b", request.Harnesses[1].Environment!["MODE"]);
            Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["trial-compare"]));
            var specPath = Path.Combine(root, "explicit.json");
            File.WriteAllText(specPath, json);
            Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["trial-compare", "--spec", specPath]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void ParseSpecRejectsHarnessOverridesOfReservedTrialEnvironment()
    {
        const string brief = "same brief";
        var digest = TrialIdentity.ComputeBriefDigest(brief);
        var json = $$"""
            {
              "sourceRepositoryPath": "repo",
              "baseCommit": "abc123",
              "workload": {
                "briefIdentity": "brief-v1",
                "briefContent": "same brief",
                "briefDigest": "{{digest}}",
                "modelIdentity": "OpenAI/gpt-test",
                "sourceProvenance": "explicit:test"
              },
              "harnesses": [
                { "name": "alpha", "fileName": "alpha.exe", "arguments": [], "environment": { "MCG_TRIAL_BASE_COMMIT": "spoofed" } },
                { "name": "beta", "fileName": "beta.exe", "arguments": [] }
              ]
            }
            """;

        var exception = Xunit.Assert.Throws<TrialComparisonUnavailableException>(() =>
            TrialCompareCliCommand.ParseSpec(json, ["trial-compare"], "receipts"));

        Xunit.Assert.Equal(TrialComparisonUnavailableReason.ReservedEnvironmentCollision, exception.Reason);
    }

    [Xunit.Fact]
    public void HistoricalResolverReturnsOneExactTupleWithoutMutatingSnapshot()
    {
        const string brief = "brief bytes\r\nkept exactly";
        var goal = HistoricalGoal(
            Dispatch(0, brief, "base-sha", "OpenAI", "gpt-a"));
        var before = JsonSerializer.Serialize(goal);

        var resolution = HistoricalTrialReplayResolver.Resolve(
            goal,
            new HistoricalTrialSelector(goal.Id, "task-1", 0),
            HistoricalTiming(goal.Id));

        Xunit.Assert.Equal("base-sha", resolution.BaseCommit);
        Xunit.Assert.Equal(brief, resolution.Workload.BriefContent);
        Xunit.Assert.Equal("OpenAI/gpt-a", resolution.Workload.ModelIdentity);
        Xunit.Assert.Equal(TrialIdentity.ComputeBriefDigest(brief), resolution.Workload.BriefDigest);
        Xunit.Assert.NotNull(resolution.Workload.HistoricalTiming);
        Xunit.Assert.Equal(before, JsonSerializer.Serialize(goal));
    }

    [Xunit.Fact]
    public void HistoricalResolverFallsBackOnlyToTheExactRecordedBriefVersion()
    {
        var dispatch = Dispatch(0, "unused snapshot", "BASE-SHA", "OpenAI", "gpt-a") with
        {
            BriefSnapshot = null
        };
        var goal = HistoricalGoal(dispatch);

        var resolution = HistoricalTrialReplayResolver.Resolve(
            goal,
            new HistoricalTrialSelector(goal.Id, "task-1", 0),
            HistoricalTiming(goal.Id));

        Xunit.Assert.Equal("brief one", resolution.Workload.BriefContent);
        Xunit.Assert.Equal("base-sha", resolution.BaseCommit);
    }

    [Xunit.Fact]
    public void ExecuteWritesTypedUnavailableReceiptWithoutCreatingRootForAmbiguousHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var specPath = Path.Combine(root, "historical.json");
            File.WriteAllText(specPath, $$"""
                {
                  "sourceRepositoryPath": "{{root.Replace("\\", "\\\\")}}",
                  "historical": { "goalId": "goal-1", "taskId": "task-1" },
                  "harnesses": [
                    { "name": "alpha", "fileName": "alpha.exe", "arguments": [] },
                    { "name": "beta", "fileName": "beta.exe", "arguments": [] }
                  ]
                }
                """);
            var goal = HistoricalGoal(
                Dispatch(0, "brief one", "base-one", "OpenAI", "gpt-a"),
                Dispatch(1, "brief two", "base-two", "OpenAI", "gpt-a"));
            var host = new FailingTrialRootHost();
            using var output = new StringWriter();

            var exception = Xunit.Assert.Throws<CliExitException>(() => TrialCompareCliCommand.Execute(
                ["trial-compare", "--spec", specPath],
                host,
                Path.Combine(root, "receipts"),
                output,
                selector => HistoricalTrialReplayResolver.Resolve(goal, selector, HistoricalTiming(goal.Id))));

            Xunit.Assert.Equal(1, exception.ExitCode);
            Xunit.Assert.Equal(0, host.CreateCalls);
            Xunit.Assert.Contains(nameof(TrialComparisonUnavailableReason.HistoricalTupleAmbiguous), output.ToString(), StringComparison.Ordinal);
            var receipt = Directory.GetFiles(Path.Combine(root, "receipts"), "preflight.json", SearchOption.AllDirectories).Single();
            Xunit.Assert.Contains(nameof(TrialComparisonUnavailableReason.HistoricalTupleAmbiguous), File.ReadAllText(receipt), StringComparison.Ordinal);

            var missingBaseGoal = HistoricalGoal(Dispatch(0, "brief one", string.Empty, "OpenAI", "gpt-a"));
            using var missingOutput = new StringWriter();
            _ = Xunit.Assert.Throws<CliExitException>(() => TrialCompareCliCommand.Execute(
                ["trial-compare", "--spec", specPath],
                host,
                Path.Combine(root, "receipts"),
                missingOutput,
                selector => HistoricalTrialReplayResolver.Resolve(missingBaseGoal, selector, HistoricalTiming(missingBaseGoal.Id))));

            Xunit.Assert.Equal(0, host.CreateCalls);
            Xunit.Assert.Contains(nameof(TrialComparisonUnavailableReason.HistoricalBaseCommitUnavailable), missingOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("text", "trial-compare succeeded=False")]
    [Xunit.InlineData("json", "\"succeeded\": false")]
    public void ExecuteRendersFailedReceiptThenExitsOne(string format, string expectedOutput)
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var specPath = WriteSpec(root);
            using var output = new StringWriter();

            var exception = Xunit.Assert.Throws<CliExitException>(() => TrialCompareCliCommand.Execute(
                ["trial-compare", "--spec", specPath, "--format", format],
                new FailingTrialRootHost(),
                Path.Combine(root, "receipts"),
                output));

            Xunit.Assert.Equal(1, exception.ExitCode);
            Xunit.Assert.Contains(expectedOutput, output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void ExecuteRejectsUnknownFormatBeforeCreatingAnyRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "trial-compare-cli-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var specPath = WriteSpec(root);
            var host = new FailingTrialRootHost();

            var exception = Xunit.Assert.Throws<ArgumentException>(() => TrialCompareCliCommand.Execute(
                ["trial-compare", "--spec", specPath, "--format", "xml"],
                host,
                Path.Combine(root, "receipts"),
                TextWriter.Null));

            Xunit.Assert.Contains("--format", exception.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, host.CreateCalls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string WriteSpec(string root)
    {
        var specPath = Path.Combine(root, "spec.json");
        const string brief = "fixture brief";
        var digest = TrialIdentity.ComputeBriefDigest(brief);
        File.WriteAllText(specPath, $$"""
            {
              "sourceRepositoryPath": "{{root.Replace("\\", "\\\\")}}",
              "baseCommit": "abc123",
              "workload": {
                "briefIdentity": "fixture-brief",
                "briefContent": "fixture brief",
                "briefDigest": "{{digest}}",
                "modelIdentity": "OpenAI/gpt-test",
                "sourceProvenance": "explicit:test"
              },
              "harnesses": [
                { "name": "alpha", "fileName": "alpha.exe", "arguments": [] },
                { "name": "beta", "fileName": "beta.exe", "arguments": [] }
              ]
            }
            """);
        return specPath;
    }

    private static string WriteHistoricalSpec(string root, string goalId)
    {
        var specPath = Path.Combine(root, $"historical-{Guid.NewGuid():N}.json");
        File.WriteAllText(specPath, $$"""
            {
              "sourceRepositoryPath": "{{root.Replace("\\", "\\\\")}}",
              "historical": { "goalId": "{{goalId}}", "taskId": "task-1", "dispatchIndex": 0 },
              "harnesses": []
            }
            """);
        return specPath;
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunAppCli(
        string workingDirectory,
        params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;
        startInfo.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app CLI.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("App CLI did not exit within 30 seconds.");
        }

        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static GoalSnapshot HistoricalGoal(params TaskDispatchSnapshot[] dispatches) => new(
        Id: "goal-1",
        Objective: "historical objective",
        Status: GoalStatus.Completed,
        Tasks:
        [
            new TaskSnapshot(
                Id: "task-1",
                Description: "historical task",
                RequiredRole: AgentRole.Developer,
                Status: WorkTaskStatus.Completed,
                AssignedAgentId: null,
                LastExecution: null,
                LastVerification: null,
                VerificationHistory: null,
                LastDispatch: dispatches.LastOrDefault(),
                LastProcess: null,
                DispatchHistory: dispatches)
        ],
        Timeline: [],
        BriefVersions:
        [
            new GoalBriefVersion(1, "brief one", DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            new GoalBriefVersion(2, "brief two", DateTimeOffset.Parse("2026-01-02T00:00:00Z"))
        ]);

    private static TaskDispatchSnapshot Dispatch(
        int index,
        string brief,
        string baseCommit,
        string provider,
        string model) => new(
            WorkerName: "worker",
            Command: "worker run",
            WorkingDirectory: "repo",
            DispatchedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(index),
            ProviderName: provider,
            ModelName: model,
            BaseCommit: baseCommit,
            BriefVersion: index + 1,
            BriefSnapshot: brief);

    private static GoalTimingReportSnapshot HistoricalTiming(string goalId) => new(
        new GoalId(goalId),
        "historical objective",
        null,
        "unavailable",
        null,
        "unavailable",
        null,
        null,
        null,
        null,
        null,
        null,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        0,
        [],
        null);

    private sealed class FailingTrialRootHost : ITrialRootHost
    {
        public int CreateCalls { get; private set; }

        public ITrialRootSession Create(TrialRootRequest request)
        {
            CreateCalls++;
            throw new InvalidOperationException("fixture trial-root creation failed");
        }
    }
}
