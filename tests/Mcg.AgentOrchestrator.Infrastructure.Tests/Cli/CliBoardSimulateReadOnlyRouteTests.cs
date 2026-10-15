using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class CliBoardSimulateReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Route_reads_empty_or_recorded_workspace_without_writing(bool records)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            if (records) WriteF1(workspace.OrchestratorDirectory);
            var before = Snapshot(root);
            var output = Run(workspace, ["board-simulate", "--day", "2026-10-15"]);
            Assert.Equal(before, Snapshot(root));
            if (records)
            {
                var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].TrimEnd('\r');
                Assert.StartsWith("CALIBRATION recorded=4 simulated=4 count_delta=0 matched=4 max_delta_min=0", last);
                Assert.EndsWith("verdict=within-bar", last);
            }
            else
            {
                Assert.Contains("no recorded gate runs", output);
                Assert.Contains("CALIBRATION recorded=0 simulated=0 count_delta=0 matched=0 max_delta_min=0 median_delta_min=0 verdict=within-bar", output);
            }
            var json = Run(workspace, ["board-simulate", "--day", "2026-10-15", "--json"]);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(records ? 4 : 0, parsed.RootElement.GetProperty("calibration").GetProperty("simulatedCount").GetInt32());
            Assert.Equal(before, Snapshot(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--width", "0")]
    [InlineData("--train-size", "2")]
    [InlineData("--drop", "bogus")]
    [InlineData("--day", "2026-13-01")]
    [InlineData("--cohorts", "maybe")]
    [InlineData("--seed", "abc")]
    [InlineData("--unknown", "x")]
    public void Invalid_policy_prints_usage_without_running_gates(string flag, string value)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var before = Snapshot(root);
            var output = "";
            var error = AsyncLocalConsoleRouter.CaptureError(() => output = Run(workspace, ["board-simulate", flag, value]));
            Assert.Equal(CliCommandHelp.BoardSimulateUsage + Environment.NewLine, error);
            Assert.Empty(output);
            Assert.DoesNotContain("GATE", output);
            Assert.Equal(before, Snapshot(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Default_day_is_the_previous_complete_UTC_day_and_duplicate_flags_are_invalid()
    {
        var root = CreateTempDirectory();
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            CliBoardSimulateQueryCommand.Execute(["board-simulate"], root, output, error,
                new DateTimeOffset(2026, 10, 16, 1, 0, 0, TimeSpan.FromHours(2)));
            Assert.Contains("day=2026-10-14", output.ToString());
            output.GetStringBuilder().Clear();
            CliBoardSimulateQueryCommand.Execute(["board-simulate", "--json", "--json"], root, output, error, At(0));
            Assert.Empty(output.ToString());
            Assert.Equal(CliCommandHelp.BoardSimulateUsage + Environment.NewLine, error.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(p => p + ":" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

    private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
    private static string Id(char goal) => new(char.ToLowerInvariant(goal), 32);
    private static string Revision => new('1', 40);

    // This CLI project is a separate assembly and cannot reference the root test project's fixture.
    private static void WriteF1(string directory)
    {
        Directory.CreateDirectory(directory);
        var members = "ABC".Select(c => new MergeTrainMemberBinding(new GoalId(Id(c)), Revision, Revision,
            [$"src/{c}.cs"], [$"resource:{c}"], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            "Clean", "NoConflictsDetected", Revision)).ToArray();
        var identity = MergeTrainIdentity.Create(members, new string('a', 40), new string('b', 40), "manifest-v1");
        var trx = Path.Combine(directory, "passed.trx");
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Fixture" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        new MergeTrainAcceptanceStore(Path.Combine(directory, "merge-train-acceptance.db"))
            .SaveGateReceipt(new("train-fixture", identity, MergeTrainGateOutcome.Passed, At(90), 20 * 60_000, [], 0, [trx], ValidForLanding: true));
        var pair = "DE".Select(c => new AcceptanceCohortMemberBinding(new GoalId(Id(c)), Revision, Revision,
            [$"src/{c}.cs"], [$"resource:{c}"], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            "Clean", "NoConflictsDetected")).ToArray();
        new CohortAcceptanceStore(Path.Combine(directory, "cohort-acceptance.db")).SaveGateReceipt(new("cohort-fixture",
            AcceptanceCohortIdentity.Create(pair, new string('a', 40), new string('b', 40), "manifest-v1"),
            AcceptanceCohortGateOutcome.Failed, At(210), 20 * 60_000, ["failed"], 1, []));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var (goal, begin, end, outcome) in new[]
        {
            ('F', 20, 70, ConductorParallelAcceptanceAttemptOutcome.Failed),
            ('E', 210, 230, ConductorParallelAcceptanceAttemptOutcome.Passed)
        })
        {
            var attempts = Path.Combine(directory, "acceptance-gate-attempts", Id(goal));
            Directory.CreateDirectory(attempts);
            var path = Path.Combine(attempts, "fixture.attempt.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new ConductorParallelAcceptanceAttempt(
                "fixture", Id(goal), Id(goal)[..8], 0, Revision, new string('a', 40), At(begin), At(end), 1,
                outcome, "", "", "", "", "", path, ScopePaths: [$"src/{goal}.cs"], CompletedAt: At(end)), options));
        }
        var events = Path.Combine(directory, "goal-events");
        Directory.CreateDirectory(events);
        foreach (var (goal, ready, leave, landed) in new[]
        { ('A', 60, 0, 92), ('B', 65, 0, 92), ('C', 70, 0, 92), ('D', 190, 215, 0), ('E', 190, 0, 232), ('F', 20, 75, 0) })
        {
            var lines = new List<string>();
            void Event(string kind, int minute) => lines.Add(JsonSerializer.Serialize(new
            {
                cursor = 1, timestamp = At(minute), goalId = Id(goal), eventType = kind,
                progressKind = kind == "TaskDispatched" ? "TaskDispatchRecorded" : "TaskVerificationRecorded",
                message = kind == "TaskDispatched" ? "Dispatched." : "Dispatch execution passed (0).", occurredAt = At(minute), taskId = "t1"
            }, options));
            Event("TaskDispatched", ready - 1);
            Event("TaskVerified", ready);
            if (leave != 0) Event("TaskDispatched", leave);
            if (landed != 0)
                foreach (var branch in new[] { "integration", "main" })
                    lines.Add(JsonSerializer.Serialize(new { cursor = 3, timestamp = At(landed).AddSeconds(branch == "main" ? 5 : 0),
                        goalId = Id(goal), eventType = "GoalLanded", integrationBranch = branch, goalBranch = "goal/" + Id(goal)[..8] }, options));
            var path = Path.Combine(events, Id(goal) + ".jsonl");
            File.WriteAllLines(path, lines);
            File.SetLastWriteTimeUtc(path, At(23 * 60).UtcDateTime);
        }
    }

    private static string Run(OrchestratorWorkspace workspace, string[] args)
    {
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var output = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        });
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.SaveAttempts);
        Assert.Equal(0, repository.ListGoalMetadataCount);
        return output;
    }
}
