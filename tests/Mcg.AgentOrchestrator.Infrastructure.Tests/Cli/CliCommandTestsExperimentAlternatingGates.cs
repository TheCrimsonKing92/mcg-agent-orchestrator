using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its database, ledgers and AsyncLocal console capture.
public sealed class CliCommandTestsExperimentAlternatingGates
{
    private static readonly DateTimeOffset Since = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = Since.AddDays(1);
    private static readonly DateTimeOffset AsOf = Since.AddDays(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Show_RecordedArmsUseMedianAndMeanDespiteNonAlternatingOrder()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var gates = new List<GateLaneTimeLedgerRecord>
            {
                Gate(record.Id, "on", 1000), Gate(record.Id, "on", 1100),
                Gate(record.Id, "on", 1200), Gate(record.Id, "on", 1300),
                Gate(record.Id, "off", 1400), Gate(record.Id, "off", 1500),
                Gate(record.Id, "off", 1300), Gate(record.Id, "off", 1600)
            };
            var ticks = new List<TickCpuLedgerRecord>
            {
                Tick(record.Id, "on", 80), Tick(record.Id, "on", 160), Tick(record.Id, "on", 240),
                Tick(record.Id, "off", 100), Tick(record.Id, "off", 200), Tick(record.Id, "off", 300)
            };
            WriteLedgers(workspace, gates, ticks);
            var expected = Show(workspace, record.Id);
            AssertMetric(expected, "gate seconds", "1450", "1150");
            AssertMetric(expected, "tick cpu ms", "200", "160");
            Assert.Contains("reading: keep (pre-registered rules evaluated)", expected);
            Assert.DoesNotContain("guardrail breached", expected);

            gates.Add(Gate(record.Id, null, 100000));
            ticks.Add(Tick(record.Id, null, 100000));
            gates.Add(Gate(record.Id, "unknown", 100000));
            ticks.Add(Tick(record.Id, "unknown", 100000));
            foreach (var arm in new[] { "on", "off" })
            {
                gates.Add(Gate("other-experiment", arm, 100000));
                ticks.Add(Tick("other-experiment", arm, 100000));
                gates.Add(Gate(record.Id, arm, 100000) with { Outcome = "failed" });
                gates.Add(Gate(record.Id, arm, 100000) with { ObservedAt = Until.AddTicks(-1) });
                ticks.Add(Tick(record.Id, arm, 100000) with { ObservedAt = Until.AddTicks(-1) });
                gates.Add(Gate(record.Id, arm, 100000) with { ObservedAt = AsOf });
                ticks.Add(Tick(record.Id, arm, 100000) with { ObservedAt = AsOf });
            }
            WriteLedgers(workspace, gates, ticks);
            Assert.Equal(expected, Show(workspace, record.Id));
        });
    }

    [Theory]
    [InlineData(2, 5, false)]
    [InlineData(5, 2, false)]
    [InlineData(3, 3, true)]
    public void Show_RequiresThreeCompletedGatesInEachArm(int on, int off, bool available)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var gates = Gates(record.Id, "on", on, 1000).Concat(Gates(record.Id, "off", off, 1500))
                .Append(Gate(record.Id, "on", 1000) with { Outcome = "failed" })
                .Append(Gate(record.Id, "off", 1500) with { Outcome = "failed" }).ToArray();
            WriteLedgers(workspace, gates, Ticks(record.Id, "on", 3, 160).Concat(Ticks(record.Id, "off", 3, 200)).ToArray());
            var output = Show(workspace, record.Id);
            AssertMetric(output, "gate seconds", available ? "1500" : "unavailable", available ? "1000" : "unavailable");
            AssertMetric(output, "tick cpu ms", available ? "200" : "unavailable", available ? "160" : "unavailable");
            Assert.Contains(available ? "reading: keep (pre-registered rules evaluated)" :
                "reading: inconclusive (unavailable comparison or zero baseline:", output);
        });
    }

    [Theory]
    [InlineData(2, 5, false)]
    [InlineData(5, 2, false)]
    [InlineData(3, 3, true)]
    public void Show_TickCpuAlsoRequiresThreeTicksInEachArm(int on, int off, bool available)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            WriteLedgers(workspace, Gates(record.Id, "on", 3, 1000).Concat(Gates(record.Id, "off", 3, 1500)).ToArray(),
                Ticks(record.Id, "on", on, 160).Concat(Ticks(record.Id, "off", off, 200)).ToArray());
            var output = Show(workspace, record.Id);
            AssertMetric(output, "gate seconds", "1500", "1000");
            AssertMetric(output, "tick cpu ms", available ? "200" : "unavailable", available ? "160" : "unavailable");
            Assert.Contains(available ? "reading: keep (pre-registered rules evaluated)" :
                "reading: inconclusive (unavailable comparison or zero baseline: tick-cpu-ms)", output);
        });
    }

    [Fact]
    public void Show_UnarmedGatesDoNotGuessAnAlternation()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            WriteLedgers(workspace, Enumerable.Range(0, 8).Select(i => Gate(record.Id, null, 1000 + i * 100)).ToArray(),
                Ticks(record.Id, "on", 3, 160).Concat(Ticks(record.Id, "off", 3, 200)).ToArray());
            var output = Show(workspace, record.Id);
            AssertMetric(output, "gate seconds", "unavailable", "unavailable");
            Assert.Contains("reading: inconclusive (unavailable comparison or zero baseline:", output);
            Assert.DoesNotContain("reading: unavailable", output);
        });
    }

    [Theory]
    [InlineData("declared")]
    [InlineData("guardrail")]
    [InlineData("keep-condition")]
    [InlineData("revert-condition")]
    [InlineData("guardrail-condition")]
    public void Show_UnsupportedMetricsAndConditionsKeepExactUnavailableReason(string scenario)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = Spec();
            spec = scenario switch
            {
                "declared" => spec with { Metrics = ["rounds-per-landing"] },
                "guardrail" => spec with { Guardrail = spec.Guardrail with { Metric = "landings-per-hour" } },
                "keep-condition" => spec with { DecisionRule = spec.DecisionRule with { KeepIf = [new("landings-per-hour", "<", -10)] } },
                "revert-condition" => spec with { DecisionRule = spec.DecisionRule with { RevertIf = [new("landings-per-hour", ">", 10)] } },
                _ => spec with { Guardrail = spec.Guardrail with { BreachIf = new("landings-per-hour", ">", 10) } }
            };
            // Stored malformed condition combinations must be rejected by reading without altering creation validation.
            var record = new ExperimentStore(workspace.ExperimentStorePath).AddAsync(spec).GetAwaiter().GetResult();
            WriteLedgers(workspace, Gates(record.Id, "on", 3, 1000).Concat(Gates(record.Id, "off", 3, 1500)).ToArray(),
                Ticks(record.Id, "on", 3, 160).Concat(Ticks(record.Id, "off", 3, 200)).ToArray());
            Assert.Contains("reading: unavailable (baseline kind alternating-gates not computable in this slice)",
                Show(workspace, record.Id));
        });
    }

    private static ExperimentSpec Spec() => CliCommandTestsExperiments.Spec() with
    {
        Baseline = new(ExperimentBaselineKind.AlternatingGates, Since, Until),
        Metrics = ["gate-seconds"],
        Guardrail = new("tick-cpu-ms", new("tick-cpu-ms", ">", 10)),
        DecisionRule = new([new("gate-seconds", "<", -10)], [new("gate-seconds", ">", 10)])
    };

    private static GateLaneTimeLedgerRecord Gate(string id, string? arm, double seconds) =>
        new(Until.AddHours(1), "fixture-attempt", "fixture-goal", "completed", seconds * 1000, seconds * 1000, 0, 0,
            Version: arm is null ? 1 : 2, ExperimentArms: arm is null ? null : new Dictionary<string, string> { [id] = arm });

    private static TickCpuLedgerRecord Tick(string id, string? arm, long cpuMs) =>
        new(Until.AddHours(1), 1, cpuMs, cpuMs, 0, 0, false,
            Version: arm is null ? 1 : 2, ExperimentArms: arm is null ? null : new Dictionary<string, string> { [id] = arm });

    private static IEnumerable<GateLaneTimeLedgerRecord> Gates(string id, string arm, int count, double seconds) =>
        Enumerable.Range(0, count).Select(_ => Gate(id, arm, seconds));

    private static IEnumerable<TickCpuLedgerRecord> Ticks(string id, string arm, int count, long cpuMs) =>
        Enumerable.Range(0, count).Select(_ => Tick(id, arm, cpuMs));

    private static void WriteLedgers(OrchestratorWorkspace workspace,
        IReadOnlyList<GateLaneTimeLedgerRecord> gates, IReadOnlyList<TickCpuLedgerRecord> ticks)
    {
        Write(GateLaneTimeLedger.ResolveStorePath(workspace.ExecutionDirectory), gates);
        Write(TickCpuLedger.ResolveStorePath(workspace.ExecutionDirectory), ticks);
    }

    private static void Write<T>(string path, IReadOnlyList<T> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
    }

    private static string Show(OrchestratorWorkspace workspace, string id) =>
        CliCommandTestsExperiments.Execute(["experiment-show", id, "--as-of", AsOf.ToString("O")], workspace);

    private static void AssertMetric(string output, string label, string baseline, string comparison) =>
        Assert.Contains($"{label}: baseline={baseline} comparison={comparison}{Environment.NewLine}", output);
}
