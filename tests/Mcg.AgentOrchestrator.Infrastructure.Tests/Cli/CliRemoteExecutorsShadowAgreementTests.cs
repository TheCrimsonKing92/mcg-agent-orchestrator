using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots, a fixed clock and AsyncLocal console capture; no shared process state.
public sealed class CliRemoteExecutorsShadowAgreementTests : CliTaskQueryTestSupport
{
    private const string Expected = "shadow_agreement\tagree=2\tdisagree=1\tincomparable=2\tunclassified=1\tincomparable_reasons={\"local-unavailable\":1,\"remote-trx-incomplete\":1}";
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RemoteExecutors_ShadowRows_AppendsCountsAndHonorsSince()
    {
        var root = CreateTempDirectory();
        try
        {
            Seed(root, ShadowRows());
            Assert.Equal(Expected, Lines(Run(root, "remote-executors")).Last());
            Assert.Equal(Expected.Replace("agree=2\t", "agree=1\t", StringComparison.Ordinal),
                Lines(Run(root, "remote-executors", "--since", "2026-10-06T00:00:01Z")).Last());
            Assert.DoesNotContain(Lines(Run(root, "remote-executors", "--since", "2026-10-06T00:01:00Z")),
                line => line.StartsWith("shadow_agreement", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RemoteExecutors_ShadowMetadata_KeepsOtherTextAndJsonBytes()
    {
        var root = CreateTempDirectory();
        var baselineRoot = CreateTempDirectory();
        try
        {
            var rows = ShadowRows();
            Seed(root, rows);
            Seed(baselineRoot, rows.Select(line =>
            {
                var row = JsonNode.Parse(line)!.AsObject();
                row.Remove("evidence_shadow");
                return row.ToJsonString();
            }));
            var actual = Lines(Run(root, "remote-executors"));
            Assert.Equal(Expected, actual.Last());
            Assert.Equal(Lines(Run(baselineRoot, "remote-executors")), actual[..^1]);
            using var baselineJson = JsonDocument.Parse(Run(baselineRoot, "remote-executors", "--json"));
            using var actualJson = JsonDocument.Parse(Run(root, "remote-executors", "--json"));
            Assert.Equal(baselineJson.RootElement.GetProperty("payload").GetRawText(),
                actualJson.RootElement.GetProperty("payload").GetRawText());
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(baselineRoot, true);
        }
    }

    [Fact]
    public void RemoteExecutors_NoShadow_KeepsLiteralHumanOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            Seed(root,
            [
                """{"observed_at":"2026-10-06T00:00:00Z","executor_id":"one","gate_attempt_id":"a","lane":"lane","outcome":"accepted","reason":null}"""
            ]);
            Assert.Equal(string.Join(Environment.NewLine, new[]
            {
                "unreadable_outcome_lines=0\tunreadable_probe_lines=0",
                "executor\tattempts\toutcomes\taccepted_share\tlast_success\tlast_probe",
                "one\t1\t{\"accepted\":1}\t1\t2026-10-06T00:00:00.0000000+00:00\tnull",
                "attempt\tone\t2026-10-06T00:00:00.0000000+00:00\ta\tlane\taccepted\t",
                "slots\tone\t2\t0\t0",
                "admission\tone\tenabled"
            }) + Environment.NewLine, Run(root, "remote-executors"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Summary_MalformedAndLegacyRows_PreservesClassificationAndTimeRules()
    {
        var root = CreateTempDirectory();
        try
        {
            var ledger = Path.Combine(root, "ledger.jsonl");
            Assert.Null(RemoteShadowAgreementSummary.Read(ledger, null));
            File.WriteAllLines(ledger,
            [
                "not-json", "[]",
                """{"evidence_shadow":"true","agreement":"agree"}""",
                """{"evidence_shadow":false,"agreement":"agree"}""",
                """{"evidence_shadow":true,"agree":true}""",
                """{"evidence_shadow":true,"observed_at":"invalid","agreement":"unknown"}""",
                """{"evidence_shadow":true,"observed_at":1,"agreement":null}""",
                """{"evidence_shadow":true,"observed_at":"2026-10-06T00:00:00Z","agreement":"incomparable"}""",
                """{"evidence_shadow":true,"observed_at":"2026-10-05T23:59:59Z","agreement":"agree"}"""
            ]);
            Assert.Equal("shadow_agreement\tagree=1\tdisagree=0\tincomparable=1\tunclassified=3\tincomparable_reasons={\"missing\":1}",
                RemoteShadowAgreementSummary.Format(RemoteShadowAgreementSummary.Read(ledger, null)!));
            Assert.Equal("shadow_agreement\tagree=0\tdisagree=0\tincomparable=1\tunclassified=0\tincomparable_reasons={\"missing\":1}",
                RemoteShadowAgreementSummary.Format(RemoteShadowAgreementSummary.Read(ledger, Start)!));
            Assert.Null(RemoteShadowAgreementSummary.Read(ledger, Start.AddSeconds(1)));
            File.WriteAllText(ledger, """{"evidence_shadow":true,"agreement":"agree"}""");
            Assert.Equal("shadow_agreement\tagree=1\tdisagree=0\tincomparable=0\tunclassified=0\tincomparable_reasons={}",
                RemoteShadowAgreementSummary.Format(RemoteShadowAgreementSummary.Read(ledger, null)!));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string[] ShadowRows() => Enumerable.Range(0, 7).Select(index =>
    {
        var row = new JsonObject
        {
            ["observed_at"] = Start.AddSeconds(index).ToString("O"),
            ["executor_id"] = "one", ["gate_attempt_id"] = "attempt-" + index,
            ["lane"] = "lane", ["outcome"] = "accepted", ["reason"] = null
        };
        if (index < 6) row["evidence_shadow"] = true;
        if (index < 2) { row["agreement"] = "agree"; row["agree"] = true; }
        else if (index == 2) { row["agreement"] = "disagree"; row["agree"] = false; }
        else if (index < 5)
        {
            row["agreement"] = "incomparable";
            row["agreement_reason"] = index == 3 ? "remote-trx-incomplete" : "local-unavailable";
        }
        else row["agree"] = true;
        return row.ToJsonString();
    }).ToArray();

    private static void Seed(string root, IEnumerable<string> rows)
    {
        var directory = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "remote-lane-executors.json"),
            """{"executors":[{"id":"one","slots":2}],"lanes":[]}""");
        File.WriteAllLines(Path.Combine(directory, RemoteExecutorHealthLedger.FileName), rows);
    }

    private static string[] Lines(string output) => output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static string Run(string root, params string[] args)
    {
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel(new FixedClock())) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var output = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, OrchestratorWorkspace.ForDirectory(root),
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        });
        Assert.Null(goal);
        return output;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Start;
    }
}
