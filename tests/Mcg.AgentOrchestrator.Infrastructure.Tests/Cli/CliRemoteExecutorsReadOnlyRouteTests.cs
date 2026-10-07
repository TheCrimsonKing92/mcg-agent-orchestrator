using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots and AsyncLocal console capture; the route never opens goal state.
public sealed class CliRemoteExecutorsReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("remote-executors")]
    [Xunit.InlineData("REMOTE-EXECUTORS")]
    [Xunit.InlineData("remote-executors", "--since", "2026-10-06T00:00:00Z")]
    [Xunit.InlineData("remote-executors", "--last", "3")]
    [Xunit.InlineData("remote-executors", "--json")]
    [Xunit.InlineData("remote-executors", "--since", "2026-10-06T00:00:00Z", "--last", "2", "--json")]
    public void EachFlagUsesQueryOnlyRouteWithoutStateOrFiles(params string[] args)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var root = CreateTempDirectory();
        try
        {
            var repository = Repository();
            Run(args, OrchestratorWorkspace.ForDirectory(root), repository);
            AssertNoState(repository);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Fact]
    public void SeededLedgersGiveSameTableAndJsonWithMalformedLinesCounted()
    {
        var root = CreateTempDirectory();
        try
        {
            var dir = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "remote-lane-executors.json"),
                """{"executors":[{"id":"one"},{"id":"zero"}],"lanes":[]}""");
            File.WriteAllLines(Path.Combine(dir, RemoteExecutorHealthLedger.FileName), [
                """{"observed_at":"2026-10-06T00:00:00Z","executor_id":"one","gate_attempt_id":"a","lane":"lane","outcome":"accepted","reason":null}""",
                """{"observed_at":"2026-10-06T01:00:00Z","executor_id":"one","gate_attempt_id":"b","lane":"lane","outcome":"unreachable","reason":"ssh failure"}""",
                "{", "{}"]);
            var probe = new RemoteExecutorProbeRow(DateTimeOffset.Parse("2026-10-06T02:00:00Z", CultureInfo.InvariantCulture),
                "one", true, 0, false, "Disabled", false, ["task-not-ready", "on-battery"], "tail");
            RemoteExecutorProbeLedger.Append(Path.Combine(dir, RemoteExecutorProbeLedger.FileName), probe);
            File.AppendAllText(Path.Combine(dir, RemoteExecutorProbeLedger.FileName), "{\n");
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var repository = Repository();
            var workspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "report");
            var table = Run(["remote-executors"], workspace, repository);
            using var json = JsonDocument.Parse(Run(["remote-executors", "--json"], workspace, repository));
            Assert.Equal(2, json.RootElement.GetProperty("unreadableOutcomeLines").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("unreadableProbeLines").GetInt32());
            Assert.Contains("unreadable_outcome_lines=2\tunreadable_probe_lines=1", table);
            var rows = json.RootElement.GetProperty("executors").EnumerateArray().ToArray();
            Assert.Equal(new[] { "one", "zero" }, rows.Select(row => row.GetProperty("executorId").GetString()));
            var tableRows = table.Split('\n').Where(line => line.StartsWith("one\t") || line.StartsWith("zero\t")).ToArray();
            foreach (var row in rows)
            {
                var id = row.GetProperty("executorId").GetString()!;
                var cells = Assert.Single(tableRows.Where(line => line.StartsWith(id + "\t", StringComparison.Ordinal))).TrimEnd('\r').Split('\t');
                Assert.Equal(row.GetProperty("attempts").GetInt32(), int.Parse(cells[1], CultureInfo.InvariantCulture));
                Assert.Equal(row.GetProperty("outcomeCounts").GetRawText(), cells[2]);
                Assert.Equal(row.GetProperty("acceptedShare").GetRawText(), cells[3]);
                Assert.Equal(row.GetProperty("lastSuccessAt").ValueKind == JsonValueKind.Null ? "null" :
                    row.GetProperty("lastSuccessAt").GetDateTimeOffset().ToString("O"), cells[4]);
                Assert.Equal(row.GetProperty("lastProbe").GetRawText(), cells[5]);
            }
            Assert.Equal(2, rows[0].GetProperty("attempts").GetInt32());
            Assert.Equal(0.5, rows[0].GetProperty("acceptedShare").GetDouble());
            Assert.Equal("Disabled", rows[0].GetProperty("lastProbe").GetProperty("taskState").GetString());
            Assert.False(rows[0].GetProperty("lastProbe").GetProperty("powerOnline").GetBoolean());
            Assert.Contains("attempt\tone\t2026-10-06T01:00:00.0000000+00:00\tb\tlane\tunreachable\tssh failure", table);
            Assert.Equal(0, rows[1].GetProperty("attempts").GetInt32());
            AssertNoState(repository);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("--bogus")]
    [Xunit.InlineData("--last", "0")]
    [Xunit.InlineData("--last", "-1")]
    [Xunit.InlineData("--last")]
    [Xunit.InlineData("--since", "not-a-date")]
    public void InvalidFlagsPrintUsage(params string[] flags)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = Repository();
            var stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
                stdout = Run(["remote-executors", .. flags], OrchestratorWorkspace.ForDirectory(root), repository));
            Assert.Equal("", stdout);
            Assert.Equal(CliCommandHelp.RemoteExecutorsUsage + Environment.NewLine, stderr);
            AssertNoState(repository);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["remote-executors", "--help"]));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ProbeStateRepository Repository() => new(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
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
    private static void AssertNoState(ProbeStateRepository repository) => Assert.Equal(0,
        repository.FullLoadAttempts + repository.LoadGoalsCount + repository.ListGoalMetadataCount +
        repository.SaveAttempts + repository.MergeSaveAttempts + repository.MutationAttempts +
        repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
}
