using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots and AsyncLocal console capture; safe to run in parallel.
public sealed class CliRemoteExecutorsReadOnlyRouteTestsLaneSeconds : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("""{"steps":[{"started_at":"2026-10-06T00:20:00Z"}],"last_status":{"seconds":"bad"}}""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"bad\"")]
    [InlineData("""{"steps":{},"last_status":[]}""")]
    [InlineData("""{"steps":[null,5,{}, {"started_at":9}, {"started_at":"bad"}],"last_status":{"seconds":null}}""")]
    [InlineData("""{"last_status":{"seconds":0}}""")]
    [InlineData("""{"last_status":{"seconds":-1}}""")]
    [InlineData("""{"last_status":{"seconds":1e400}}""")]
    public void LaneSecondsAndSlotsUseQueryOnlyRouteAndMalformedAttemptsStayReadable(string malformedAttempt)
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "remote-lane-executors.json"),
                """{"executors":[{"id":"one","slots":2},{"id":"zero","slots":1}],"lanes":[]}""");
            File.WriteAllLines(Path.Combine(dir, RemoteExecutorHealthLedger.FileName),
            [
                """{"observed_at":"2026-10-06T00:10:00Z","executor_id":"one","gate_attempt_id":"a","lane":"lane","outcome":"accepted","reason":null,"attempt":{"steps":[{"started_at":"2026-10-06T00:16:00Z"},null,{"started_at":"bad"},{"started_at":"2026-10-06T00:00:00Z"}],"last_status":{"seconds":200}}}""",
                """{"observed_at":"2026-10-06T00:15:00Z","executor_id":"one","gate_attempt_id":"b","lane":"lane","outcome":"accepted","reason":null,"attempt":{"steps":[{"started_at":"2026-10-06T00:05:00Z"}],"last_status":{"seconds":300}}}""",
                """{"observed_at":"2026-10-06T00:30:00Z","executor_id":"one","gate_attempt_id":"c","lane":"lane","outcome":"accepted","reason":null,"attempt":ATTEMPT}""".Replace("ATTEMPT", malformedAttempt)
            ]);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var workspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "report");
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["remote-executors"]));
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["remote-executors", "--json"]));
            var table = Run(["remote-executors"], workspace, repository);
            using var json = JsonDocument.Parse(Run(["remote-executors", "--json"], workspace, repository));
            Assert.Equal(0, json.RootElement.GetProperty("unreadableOutcomeLines").GetInt32());
            Assert.Contains("unreadable_outcome_lines=0\tunreadable_probe_lines=0", table);
            var rows = json.RootElement.GetProperty("executors").EnumerateArray().ToArray();
            Assert.Equal(new[] { "one", "zero" }, rows.Select(row => row.GetProperty("executorId").GetString()));
            var one = rows[0];
            Assert.Equal(2, one.GetProperty("configuredSlots").GetInt32());
            Assert.Equal(2, one.GetProperty("peakConcurrentAttempts").GetInt32());
            Assert.Equal(2, one.GetProperty("concurrentAttempts").GetInt32());
            var lane = Assert.Single(one.GetProperty("lanes").EnumerateArray());
            Assert.Equal("lane", lane.GetProperty("lane").GetString());
            Assert.Equal(2, lane.GetProperty("samples").GetInt32());
            Assert.Equal(250, lane.GetProperty("medianSeconds").GetDouble());
            Assert.Equal(300, lane.GetProperty("lastSeconds").GetDouble());
            Assert.Equal(JsonValueKind.Null, lane.GetProperty("soloMedianSeconds").ValueKind);
            Assert.Equal(250, lane.GetProperty("concurrentMedianSeconds").GetDouble());
            var lines = table.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            Assert.Contains("slots\tone\t2\t2\t2", lines);
            Assert.Contains("lane\tone\tlane\t2\t250\t300\tnull\t250", lines);
            Assert.Contains("slots\tzero\t1\t0\t0", lines);
            Assert.Equal(Array.IndexOf(lines, "slots\tone\t2\t2\t2") + 1,
                Array.IndexOf(lines, "lane\tone\tlane\t2\t250\t300\tnull\t250"));
            Assert.StartsWith("attempt\tone\t", lines[Array.IndexOf(lines, "slots\tone\t2\t2\t2") - 1]);
            Assert.Equal(3, one.GetProperty("attempts").GetInt32());
            Assert.Equal("{\"accepted\":3}", one.GetProperty("outcomeCounts").GetRawText());
            Assert.Equal(1, one.GetProperty("acceptedShare").GetDouble());
            Assert.Equal(JsonValueKind.Null, one.GetProperty("lastProbe").ValueKind);
            Assert.Equal("2026-10-06T00:30:00+00:00", one.GetProperty("lastSuccessAt").GetString());
            foreach (var attempt in one.GetProperty("recentAttempts").EnumerateArray())
                Assert.Equal(new[] { "observedAt", "executorId", "gateAttemptId", "lane", "outcome", "reason" },
                    attempt.EnumerateObject().Select(property => property.Name));
            Assert.Contains("one\t3\t{\"accepted\":3}\t1\t2026-10-06T00:30:00.0000000+00:00\tnull", lines);
            Assert.Contains("attempt\tone\t2026-10-06T00:10:00.0000000+00:00\ta\tlane\taccepted\t", lines);
            Assert.Empty(rows[1].GetProperty("lanes").EnumerateArray());
            Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.ListGoalMetadataCount +
                repository.SaveAttempts + repository.MergeSaveAttempts + repository.MutationAttempts +
                repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Run(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository repository)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var stdout = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        });
        Assert.Null(goal);
        return stdout;
    }
}
