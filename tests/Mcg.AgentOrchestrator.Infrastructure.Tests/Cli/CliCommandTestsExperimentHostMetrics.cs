using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its workspace, ledgers and AsyncLocal console capture.
public sealed class CliCommandTestsExperimentHostMetrics
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Until = Since.AddDays(1);
    private static readonly DateTimeOffset AsOf = Since.AddDays(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Show_WindowMetricsUseCompletedGateMedianAndMeanCpuAndReadKeep()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            WriteLedgers(workspace,
            [
                Gate(Since, 1100), Gate(Since.AddHours(1), 1200), Gate(Since.AddHours(2), 1300),
                Gate(Since.AddHours(3), 5000, "failed"),
                Gate(Until, 900), Gate(Until.AddHours(1), 1000),
                Gate(Until.AddHours(2), 1050), Gate(Until.AddHours(3), 1100),
                Gate(Since.AddTicks(-1), 100000), Gate(AsOf, 100000)
            ],
            [
                Tick(Since, 100), Tick(Since.AddHours(1), 300), Tick(Until, 150),
                Tick(Since.AddTicks(-1), 100000), Tick(AsOf, 100000)
            ]);
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var output = Show(workspace, record.Id);

            AssertMetric(output, "gate seconds", "1200", "1025");
            AssertMetric(output, "tick cpu ms", "200", "150");
            Assert.Contains("reading: keep (pre-registered rules evaluated)", output);
            Assert.DoesNotContain("guardrail breached", output);
        });
    }

    [Fact]
    public void Show_MissingLedgersReportUnavailableAndInconclusive()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Assert.False(File.Exists(GateLaneTimeLedger.ResolveStorePath(workspace.ExecutionDirectory)));
            Assert.False(File.Exists(TickCpuLedger.ResolveStorePath(workspace.ExecutionDirectory)));
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var output = Show(workspace, record.Id);

            AssertMetric(output, "gate seconds", "unavailable", "unavailable");
            AssertMetric(output, "tick cpu ms", "unavailable", "unavailable");
            Assert.Contains("reading: inconclusive (unavailable comparison or zero baseline: gate-seconds, tick-cpu-ms)", output);
        });
    }

    [Fact]
    public void Show_MalformedLinesBetweenValidSamplesLeaveReadingUnchanged()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            WriteLedgers(workspace, [Gate(Since, 1200), Gate(Until, 1000)],
                [Tick(Since, 200), Tick(Until, 150)]);
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var expected = Show(workspace, record.Id);
            AssertMetric(expected, "gate seconds", "1200", "1000");
            AssertMetric(expected, "tick cpu ms", "200", "150");

            InsertBadLine(GateLaneTimeLedger.ResolveStorePath(workspace.ExecutionDirectory));
            InsertBadLine(TickCpuLedger.ResolveStorePath(workspace.ExecutionDirectory));

            Assert.Equal(expected, Show(workspace, record.Id));
        });
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.TwinGoal, "gate-seconds")]
    [InlineData(ExperimentBaselineKind.TwinGoal, "tick-cpu-ms")]
    [InlineData(ExperimentBaselineKind.GoalCohort, "gate-seconds")]
    [InlineData(ExperimentBaselineKind.GoalCohort, "tick-cpu-ms")]
    public void Show_TwinAndCohortHostMetricsAreUnavailableWithLandedTotals(
        ExperimentBaselineKind kind, string metric)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = CliCommandTestsExperimentReading.Seed(workspace);
            var before = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("aaaa", StringComparison.Ordinal));
            var after = kernel.Goals.Single(goal => goal.Id.Value.StartsWith("cccc", StringComparison.Ordinal));
            // Nonempty landed totals force TotalsValue to reject window metrics if the null branch is removed.
            Assert.True(RoundValueReport.Build([before], Since, AsOf).Window.LandedGoals > 0);
            Assert.True(RoundValueReport.Build([after], Since, AsOf).Window.LandedGoals > 0);
            WriteLedgers(workspace, [Gate(Since, 1200), Gate(Until, 1000)],
                [Tick(Since, 200), Tick(Until, 150)]);
            var baseline = kind == ExperimentBaselineKind.TwinGoal
                ? new ExperimentBaseline(kind, TwinGoalId: "aaaa", ComparisonGoalId: "cccc")
                : new ExperimentBaseline(kind, BaselineGoalIds: ["aaaa"], ComparisonGoalIds: ["cccc"]);
            var spec = Spec() with
            {
                Baseline = baseline, Metrics = [metric],
                DecisionRule = new([new(metric, "<=", -5)], [new(metric, ">=", 5)]),
                Guardrail = new("productive-rounds", new("productive-rounds", ">", 1000))
            };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var output = Show(workspace, record.Id);

            AssertMetric(output, metric.Replace('-', ' '), "unavailable", "unavailable");
            Assert.Contains($"reading: inconclusive (unavailable comparison or zero baseline: {metric})", output);
        });
    }

    private static ExperimentSpec Spec() => CliCommandTestsExperiments.Spec() with
    {
        Metrics = ["gate-seconds"],
        Guardrail = new("tick-cpu-ms", new("tick-cpu-ms", ">", 10)),
        DecisionRule = new([new("gate-seconds", "<=", -5)], [new("gate-seconds", ">=", 5)])
    };

    private static GateLaneTimeLedgerRecord Gate(DateTimeOffset at, double seconds, string outcome = "completed") =>
        new(at, "fixture-attempt", "fixture-goal", outcome, seconds * 1000, seconds * 1000, 0, 0);

    private static TickCpuLedgerRecord Tick(DateTimeOffset at, long cpuMs) =>
        new(at, 1, cpuMs, cpuMs, 0, 0, false);

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

    private static void InsertBadLine(string path)
    {
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        File.WriteAllLines(path, [lines[0], "not-json", lines[1]]);
    }

    private static string Show(OrchestratorWorkspace workspace, string id) =>
        CliCommandTestsExperiments.Execute(["experiment-show", id, "--as-of", AsOf.ToString("O")], workspace);

    private static void AssertMetric(string output, string label, string baseline, string comparison) =>
        Assert.Contains($"{label}: baseline={baseline} comparison={comparison}{Environment.NewLine}", output);
}
