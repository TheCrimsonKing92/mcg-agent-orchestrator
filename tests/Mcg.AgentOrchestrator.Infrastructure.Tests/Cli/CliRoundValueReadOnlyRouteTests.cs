using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: per-test probe, isolated temporary root, AsyncLocal console capture and fixed windows.
public sealed class CliRoundValueReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private static readonly string[] WindowArgs =
        ["round-value", "--since", "2026-09-24T00:00:00Z", "--until", "2026-09-26T00:00:00Z"];

    [Theory]
    [InlineData("round-value")]
    [InlineData("ROUND-VALUE")]
    [InlineData("round-value", "--since", "2026-09-24T00:00:00Z")]
    [InlineData("round-value", "--until", "2026-09-26T00:00:00Z")]
    [InlineData("round-value", "--json")]
    [InlineData("round-value", "--baseline-since", "2026-09-24T00:00:00Z", "--baseline-until", "2026-09-25T00:00:00Z")]
    public void IsReadOnlyCommand_EachSupportedForm_IsQueryOnly(params string[] args)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
    }

    [Fact]
    public void TryExecute_Fixture_ExactTextAndJsonWithoutWrites()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(Fixture.Create()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            string Run(string[] command) => CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(command, repository, workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
                Assert.False(changed);
            });

            var table = Run(WindowArgs);
            var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            string[] expectedLines =
            [
                "Round value [2026-09-24T00:00:00.0000000+00:00, 2026-09-26T00:00:00.0000000+00:00) | cohort = landed or lost goals whose last round is in the window",
                "Day | Landed | Lost | Rounds | Productive | Overhead | Wasted | Rounds per landing | Waste share | Input | Cached input | Output | Usage unreported",
                "2026-09-24 | 1 | 0 | 8 | 4 | 2 | 2 | 8 | 0.25 | 1000 | 400 | 100 | 7",
                "2026-09-25 | 0 | 1 | 2 | 0 | 0 | 2 | n/a | 1 | 500 | 200 | 50 | 1",
                "Window | 1 | 1 | 10 | 4 | 2 | 4 | 10 | 0.4 | 1500 | 600 | 150 | 8",
                "Waste cause | Rounds | Share",
                "abandoned-goal | 2 | 0.2",
                "flake-or-apparatus | 1 | 0.1",
                "unchanged-commit-review | 1 | 0.1",
                "Pending goals | 1 | rounds=2"
            ];
            Assert.Equal(expectedLines, lines);
            Assert.Equal(string.Join(Environment.NewLine, expectedLines) + Environment.NewLine, table);
            Assert.DoesNotContain(lines, l => l.StartsWith("2026-09-26", StringComparison.Ordinal));

            var jsonOutput = Run([.. WindowArgs, "--json"]);
            using var json = JsonDocument.Parse(jsonOutput);
            var report = json.RootElement;
            Assert.Equal(["since", "until", "days", "window", "wasteByCause", "cascadeRoutes", "pendingGoals", "pendingRounds"],
                report.EnumerateObject().Select(p => p.Name));
            Assert.Equal(Fixture.Since, report.GetProperty("since").GetDateTimeOffset());
            Assert.Equal(Fixture.Until, report.GetProperty("until").GetDateTimeOffset());
            var totals = report.GetProperty("window");
            Assert.Equal(1, totals.GetProperty("landedGoals").GetInt32());
            Assert.Equal(1, totals.GetProperty("lostGoals").GetInt32());
            Assert.Equal(10, totals.GetProperty("rounds").GetInt32());
            Assert.Equal(4, totals.GetProperty("productive").GetInt32());
            Assert.Equal(2, totals.GetProperty("expectedOverhead").GetInt32());
            Assert.Equal(4, totals.GetProperty("wasted").GetInt32());
            Assert.Equal(10, totals.GetProperty("roundsPerLanding").GetDouble());
            Assert.Equal(0.4, totals.GetProperty("wasteShare").GetDouble());
            Assert.Equal(1500, totals.GetProperty("inputTokens").GetInt64());
            Assert.Equal(600, totals.GetProperty("cachedInputTokens").GetInt64());
            Assert.Equal(150, totals.GetProperty("outputTokens").GetInt64());
            Assert.Equal(8, totals.GetProperty("usageUnreported").GetInt32());
            var days = report.GetProperty("days").EnumerateArray().ToArray();
            Assert.Equal(["2026-09-24", "2026-09-25"], days.Select(d => d.GetProperty("day").GetString()));
            Assert.Equal(JsonValueKind.Null, days[1].GetProperty("roundsPerLanding").ValueKind);
            Assert.Equal(1, days[1].GetProperty("wasteShare").GetDouble());
            var causes = report.GetProperty("wasteByCause").EnumerateArray().ToArray();
            Assert.Equal(["abandoned-goal", "flake-or-apparatus", "unchanged-commit-review"],
                causes.Select(c => c.GetProperty("cause").GetString()));
            Assert.Equal([2, 1, 1], causes.Select(c => c.GetProperty("rounds").GetInt32()));
            Assert.Equal([0.2, 0.1, 0.1], causes.Select(c => c.GetProperty("share").GetDouble()));
            Assert.Equal(1, report.GetProperty("pendingGoals").GetInt32());
            Assert.Equal(2, report.GetProperty("pendingRounds").GetInt32());
            Assert.Equal(2, repository.ListGoalMetadataCount);
            Assert.Equal(2, repository.LoadGoalsCount);
            Assert.Equal(4, repository.LoadedGoalIds.Distinct().Count());
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);
            Assert.Null(goal);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--since")]
    [InlineData("--until")]
    [InlineData("--baseline-since")]
    [InlineData("--baseline-until")]
    [InlineData("--baseline-since", "2026-09-24T00:00:00Z")]
    [InlineData("--baseline-until", "2026-09-25T00:00:00Z")]
    [InlineData("--baseline-since", "invalid", "--baseline-until", "2026-09-25T00:00:00Z")]
    [InlineData("--baseline-since", "2026-09-24T00:00:00", "--baseline-until", "2026-09-25T00:00:00Z")]
    [InlineData("--baseline-since", "2026-09-25T00:00:00Z", "--baseline-until", "2026-09-25T00:00:00Z")]
    [InlineData("--baseline-since", "2026-09-26T00:00:00Z", "--baseline-until", "2026-09-25T00:00:00Z")]
    [InlineData("--since", "invalid")]
    [InlineData("--since", "2026-09-24")]
    [InlineData("--since", "2026-09-24T00:00:00")]
    [InlineData("--since", "2026-09-26T00:00:00Z", "--until", "2026-09-26T00:00:00Z")]
    [InlineData("--since", "2026-09-27T00:00:00Z", "--until", "2026-09-26T00:00:00Z")]
    public void TryExecute_InvalidArguments_OnlyUsageAndNoStateReads(params string[] flags)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(Fixture.Create()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            var stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() => stdout = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(["round-value", .. flags], repository,
                    OrchestratorWorkspace.ForDirectory(root), new InMemoryModelProviderRegistry([]),
                    null, ref agents, ref profiles, ref goal, out var changed));
                Assert.False(changed);
            }));
            Assert.Equal("", stdout);
            Assert.Equal(CliCommandHelp.RoundValueUsage + Environment.NewLine, stderr);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListGoalMetadataCount);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryExecute_EmptyGoals_EmitsZeroWindowWithoutSelectedLoad()
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            var output = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(WindowArgs, repository,
                    OrchestratorWorkspace.ForDirectory(root), new InMemoryModelProviderRegistry([]),
                    null, ref agents, ref profiles, ref goal, out var changed));
                Assert.False(changed);
            });
            Assert.Contains("Window | 0 | 0 | 0 | 0 | 0 | 0 | n/a | 0 | 0 | 0 | 0 | 0", output);
            Assert.Contains("Pending goals | 0 | rounds=0", output);
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(0, repository.LoadGoalsCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsReadOnlyCommand_Help_DeclinesToCommandHelp()
    {
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["round-value", "--help"]));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["round-value", "-h"]));
    }

    [Fact]
    public void TryExecute_Baseline_EmitsComparisonInTextAndJsonWithoutWrites()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(Fixture.Create()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            string Run(string[] command) => CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(command, repository, workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
                Assert.False(changed);
            });
            string[] current = ["round-value", "--since", "2026-09-25T00:00:00Z", "--until", "2026-09-26T00:00:00Z"];
            string[] args = [.. current, "--baseline-since", "2026-09-24T00:00:00Z", "--baseline-until", "2026-09-25T00:00:00Z"];
            var singleWindow = Run(current);
            var text = Run(args);
            Assert.StartsWith(singleWindow, text);
            Assert.Equal(string.Join(Environment.NewLine,
            [
                "Baseline [2026-09-24T00:00:00.0000000+00:00, 2026-09-25T00:00:00.0000000+00:00) | deltas = current minus baseline",
                "Waste cause | Current rounds | Current share | Baseline rounds | Baseline share | Rounds delta | Share delta",
                "abandoned-goal | 2 | 1 | 0 | 0 | 2 | 1",
                "flake-or-apparatus | 0 | 0 | 1 | 0.125 | -1 | -0.125",
                "unchanged-commit-review | 0 | 0 | 1 | 0.125 | -1 | -0.125",
                "Rounds per landing | current=n/a | baseline=8", ""
            ]), text[singleWindow.Length..]);

            using var singleJson = JsonDocument.Parse(Run([.. current, "--json"]));
            using var json = JsonDocument.Parse(Run([.. args, "--json"]));
            foreach (var property in singleJson.RootElement.EnumerateObject())
                Assert.Equal(property.Value.GetRawText(), json.RootElement.GetProperty(property.Name).GetRawText());
            var comparison = json.RootElement.GetProperty("baseline");
            Assert.Equal(Fixture.Since, comparison.GetProperty("window").GetProperty("since").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, comparison.GetProperty("current").GetProperty("roundsPerLanding").ValueKind);
            Assert.Equal(8, comparison.GetProperty("baseline").GetProperty("roundsPerLanding").GetDouble());
            var causes = comparison.GetProperty("causes").EnumerateArray().ToArray();
            Assert.Equal(["abandoned-goal", "flake-or-apparatus", "unchanged-commit-review"],
                causes.Select(c => c.GetProperty("cause").GetString()));
            Assert.Equal([2, 0, 0], causes.Select(c => c.GetProperty("currentRounds").GetInt32()));
            Assert.Equal([1.0, 0, 0], causes.Select(c => c.GetProperty("currentShare").GetDouble()));
            Assert.Equal([0, 1, 1], causes.Select(c => c.GetProperty("baselineRounds").GetInt32()));
            Assert.Equal([0, 0.125, 0.125], causes.Select(c => c.GetProperty("baselineShare").GetDouble()));
            Assert.Equal([2, -1, -1], causes.Select(c => c.GetProperty("roundsDelta").GetInt32()));
            Assert.Equal([1, -0.125, -0.125], causes.Select(c => c.GetProperty("shareDelta").GetDouble()));
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static class Fixture
    {    
        internal static readonly DateTimeOffset Since = At("2026-09-24T00:00:00Z");
        internal static readonly DateTimeOffset Until = At("2026-09-26T00:00:00Z");
    
        internal static AgentOrchestratorKernel Create() => AgentOrchestratorKernel.FromSnapshot(
            new OrchestratorSnapshot(
            [
                new GoalSnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", GoalStatus.Completed,
                [
                    Task("a-plan", AgentRole.Planner, WorkTaskStatus.Completed,
                        Dispatch("2026-09-24T01:00:00Z")),
                    Task("a-dev", AgentRole.Developer, WorkTaskStatus.Completed,
                        Dispatch("2026-09-24T02:00:00Z", "c1", 1000, 400, 100),
                        Dispatch("2026-09-24T04:15:00Z", "c2")),
                    Task("a-test", AgentRole.Tester, WorkTaskStatus.Completed,
                        Dispatch("2026-09-24T02:40:00Z"),
                        Dispatch("2026-09-24T03:00:00Z"), Dispatch("2026-09-24T03:00:00Z")),
                    Task("a-rev", AgentRole.Reviewer, WorkTaskStatus.Completed,
                        Dispatch("2026-09-24T03:00:00Z", "c2"),
                        Dispatch("2026-09-24T05:00:00Z", "c3"), Dispatch("2026-09-24T06:00:00Z", "c3"))
                ],
                [
                    Event('a', "a-plan", "2026-09-24T01:30:00Z", ProgressKind.TaskCompleted, "done"),
                    Event('a', "a-dev", "2026-09-24T02:30:00Z", ProgressKind.TaskCompleted, "done"),
                    Event('a', "a-dev", "2026-09-24T04:10:00Z", ProgressKind.TaskRetried, "auto-review-retry round 1: finding"),
                    Event('a', "a-dev", "2026-09-24T04:45:00Z", ProgressKind.TaskCompleted, "done"),
                    Event('a', "a-test", "2026-09-24T02:50:00Z", ProgressKind.TaskFailed, "inconclusive"),
                    Event('a', "a-test", "2026-09-24T02:55:00Z", ProgressKind.TaskRetried, "Auto-retry verification-inconclusive Tester task"),
                    Event('a', "a-test", "2026-09-24T03:20:00Z", ProgressKind.TaskCompleted, "done"),
                    Event('a', "a-rev", "2026-09-24T03:30:00Z", ProgressKind.TaskFailed, "finding"),
                    Event('a', "a-rev", "2026-09-24T04:50:00Z", ProgressKind.TaskRetried, "Invalidated after upstream Developer change"),
                    Event('a', "a-rev", "2026-09-24T05:30:00Z", ProgressKind.TaskCompleted, "done"),
                    Event('a', "a-rev", "2026-09-24T05:50:00Z", ProgressKind.TaskRetried, "Invalidated after upstream Developer change"),
                    Event('a', "a-rev", "2026-09-24T06:30:00Z", ProgressKind.TaskCompleted, "done")
                ]),
                new GoalSnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B", GoalStatus.Cancelled,
                [
                    Task("b-dev", AgentRole.Developer, WorkTaskStatus.Completed,
                        Dispatch("2026-09-25T01:00:00Z", input: 500, cached: 200, output: 50),
                        Dispatch("2026-09-25T01:20:00Z"))
                ],
                [
                    Event('b', "b-dev", "2026-09-25T01:10:00Z", ProgressKind.TaskFailed, "provider"),
                    Event('b', "b-dev", "2026-09-25T01:15:00Z", ProgressKind.TaskRetried, "Dispatch hit provider connectivity failure"),
                    Event('b', "b-dev", "2026-09-25T01:50:00Z", ProgressKind.TaskCompleted, "done")
                ]),
                new GoalSnapshot("cccccccccccccccccccccccccccccccc", "C", GoalStatus.Active,
                [
                    Task("c-dev", AgentRole.Developer, WorkTaskStatus.Running,
                        Dispatch("2026-09-25T02:00:00Z"), Dispatch("2026-09-25T03:00:00Z"))
                ], []),
                new GoalSnapshot("dddddddddddddddddddddddddddddddd", "D", GoalStatus.Completed,
                [
                    Task("d-plan", AgentRole.Planner, WorkTaskStatus.Completed, Dispatch("2026-09-25T05:00:00Z")),
                    Task("d-dev", AgentRole.Developer, WorkTaskStatus.Completed, Dispatch("2026-09-26T01:00:00Z"))
                ],
                [
                    Event('d', "d-plan", "2026-09-25T05:30:00Z", ProgressKind.TaskCompleted, "done"),
                    Event('d', "d-dev", "2026-09-26T01:30:00Z", ProgressKind.TaskCompleted, "done")
                ])
            ], []));
    
        internal static DateTimeOffset At(string text) =>
            DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    
        internal static TaskSnapshot Task(string id, AgentRole role, WorkTaskStatus status,
            params TaskDispatchSnapshot[] dispatches) =>
            new(id, id, role, status, null, null, null, [], null, null, DispatchHistory: dispatches);
    
        internal static TaskDispatchSnapshot Dispatch(string at, string? commit = null,
            long? input = null, long? cached = null, long? output = null) =>
            new("worker", "command", "root", At(at), BaseCommit: commit,
                ContextPackageReceipt: input is null && cached is null && output is null ? null :
                    new WorkerContextPackageReceipt("package", [], Usage(input), Usage(cached), Usage(output)));
    
        private static ProviderUsageValue Usage(long? count) => count is { } value
            ? ProviderUsageValue.Reported(value) : ProviderUsageValue.Unknown("not reported");
    
        private static ProgressEventSnapshot Event(char goal, string task, string at, ProgressKind kind, string message) =>
            new(new string(goal, 32), task, kind, message, At(at));
    
    }
}
