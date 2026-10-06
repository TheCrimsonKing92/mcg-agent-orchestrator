using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: private roots and AsyncLocal console capture; no shared mutable state.
public sealed class CliLaneReuseShadowReadOnlyRouteTestsRuleSummary : CliTaskQueryTestSupport
{
    private static readonly string[] Window = ["--since", "2026-09-29T00:00:00Z", "--until", "2026-10-06T00:00:00Z"];

    [Fact]
    public void RecordedV2AndVerdicts_AppendRuleLinesMatchingJsonWithoutWrites()
    {
        var root = CreateTempDirectory();
        try
        {
            Write(root, "paired", """
                {"recorded_at":"2026-10-05T00:00:00Z","lanes":[
                  {"lane":"a","decision":"would-reuse","reason":"unaffected","executed":true,"duration_ms":1000,"verdict":"RED","rule_v2":{"decision":"must-run","reason":"held"}},
                  {"lane":"b","decision":"must-run","reason":"held","executed":true,"duration_ms":2000,"verdict":"RED","rule_v2":{"decision":"would-reuse","reason":"unaffected"}},
                  {"lane":"c","decision":"would-reuse","reason":"unaffected","executed":false,"duration_ms":9000,"verdict":"RED","rule_v2":{"decision":"would-reuse","reason":"unaffected"}}
                ]}
                """);
            Write(root, "legacy", """
                {"recorded_at":"2026-10-04T00:00:00Z","lanes":[
                  {"lane":"old","decision":"would-reuse","reason":"unaffected","executed":true,"duration_ms":5000,"verdict":"RED"}
                ]}
                """);
            var snapshot = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var table = Run(["lane-reuse-shadow", .. Window], workspace, repository);
            using var json = JsonDocument.Parse(Run(["lane-reuse-shadow", .. Window, "--json"], workspace, repository));
            var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
            var rules = json.RootElement.GetProperty("ruleSummaries").EnumerateArray().ToArray();
            Assert.Equal(2, rules.Length);
            Assert.Equal(2, lines.Count(line => line.StartsWith("rule\t", StringComparison.Ordinal)));
            var expected = new[] {
                new LaneReuseShadowRuleSummary("marker-v1", 3, 2, 0.6667, 1, 1),
                new LaneReuseShadowRuleSummary("launch-contract-v2", 3, 2, 0.6667, 2, 1)
            };
            for (var i = 0; i < rules.Length; i++)
            {
                var fields = lines[^(rules.Length - i)].Split('\t');
                Assert.Equal("rule", fields[0]);
                Assert.Equal(expected[i].Rule, fields[1]);
                Assert.Equal(expected[i], JsonSerializer.Deserialize<LaneReuseShadowRuleSummary>(rules[i],
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                var values = fields.Skip(2).ToDictionary(field => field.Split('=')[0],
                    field => double.Parse(field.Split('=')[1], CultureInfo.InvariantCulture));
                foreach (var (tableKey, jsonKey) in new[] {
                    ("paired_lane_rows", "pairedLaneRows"), ("would_reuse_rows", "wouldReuseRows"),
                    ("would_reuse_share", "wouldReuseShare"), ("saved_lane_seconds", "savedLaneSeconds"), ("misses", "misses") })
                    Assert.Equal(rules[i].GetProperty(jsonKey).GetDouble(), values[tableKey]);
                Assert.Equal(1, values["records_without_rule_v2"]);
            }
            Assert.Equal(1, json.RootElement.GetProperty("recordsWithoutRuleV2").GetInt32());
            Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.ListGoalMetadataCount +
                repository.SaveAttempts + repository.MergeSaveAttempts + repository.MutationAttempts +
                repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
            Assert.Equal(snapshot.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in snapshot) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("\"rule_v2\":42")]
    [InlineData("\"rule_v2\":{\"decision\":42}")]
    [InlineData("\"verdict\":42")]
    public void MalformedAddedFields_CountRecordAsUnreadable(string field)
    {
        var root = CreateTempDirectory();
        try
        {
            Write(root, "bad", "{\"recorded_at\":\"2026-10-05T00:00:00Z\",\"lanes\":[{" +
                "\"lane\":\"a\",\"decision\":\"would-reuse\",\"executed\":true," + field + "}]}");
            using var document = JsonDocument.Parse(Run(["lane-reuse-shadow", .. Window, "--json"],
                OrchestratorWorkspace.ForDirectory(root), new ProbeStateRepository(new AgentOrchestratorKernel())));
            Assert.Equal(1, document.RootElement.GetProperty("unreadableRecords").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("gates").GetInt32());
        }
        finally { Directory.Delete(root, recursive: true); }
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
    private static void Write(string root, string attempt, string record)
    {
        var directory = Path.Combine(root, ".orchestrator", "lane-reuse-shadow", "goal");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, attempt + ".json"), record);
    }
}
