using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its workspace, logs and databases; time and console capture are explicit.
public sealed class CliCommandTestsExperimentGateLedgerProgress
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = Start.AddDays(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Show_FullCompletedLedgerRuns_CountFourInsteadOfTwelveEvents()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, GateSpec());
            WriteLedgers(workspace, ComparisonRows());
            WriteAttempts(workspace);
            var ledgers = ExperimentHostLedgers.Read(workspace.ExecutionDirectory);
            Assert.Equal(12, ConductCount(workspace, record));
            var count = ExperimentGateStopCount.Count(record, AsOf, ledgers,
                () => throw new InvalidOperationException("Ledger count must not read conduct attempts."));
            var reading = ExperimentReading.Evaluate(record, [], new Dictionary<string, DateTimeOffset>(), [],
                AsOf, count, ledgers);

            Assert.Equal(4, reading.ObservedCount);
            Assert.True(reading.StopRuleMet);
            Assert.Equal(1025d, ledgers.GateSeconds(Start, AsOf, 300));
            Assert.Contains("stop rule: 4 of 4 gates (met)", Show(workspace, record.Id));
        });
    }

    [Fact]
    public void ObserveTick_ThreeThenFourFullRuns_RaisesStopRuleOnlyOnce()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, GateSpec());
            var rows = ComparisonRows().Where(row => row.TotalMs != 1100 * 1000).ToList();
            WriteLedgers(workspace, rows);
            WriteAttempts(workspace);
            Assert.Equal(12, ConductCount(workspace, record));
            Assert.Contains("stop rule: 3 of 4 gates (not met)", Show(workspace, record.Id));
            var watch = new ConductorExperimentWatch(workspace,
                new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => AsOf));

            Assert.Empty(AsyncLocalConsoleRouter.Capture(() => watch.ObserveTick(AsOf)));
            Assert.Empty(ReadingDueEvents(workspace));
            Assert.Empty(Items(workspace));

            rows.Add(Gate(Start.AddHours(4), 1100));
            WriteLedgers(workspace, rows);
            Assert.Contains("stop rule: 4 of 4 gates (met)", Show(workspace, record.Id));
            Assert.Empty(AsyncLocalConsoleRouter.Capture(() => watch.ObserveTick(AsOf)));
            Assert.Contains($"experiment={record.Id} trigger=stop-rule observed=4 stopRuleMet=true",
                Assert.Single(ReadingDueEvents(workspace)).GetProperty("detail").GetString());
            var item = Assert.Single(Items(workspace));
            Assert.Equal(CollaborationItemType.Decision, item.Type);
            Assert.Equal($"experiment-reading-due:{record.Id}:stop-rule", item.CorrelationKey);

            Assert.Empty(AsyncLocalConsoleRouter.Capture(() => watch.ObserveTick(AsOf)));
            Assert.Single(ReadingDueEvents(workspace));
            Assert.Equal(item.Id, Assert.Single(Items(workspace)).Id);
        });
    }

    [Fact]
    public void CompletedGateCount_MinimumAndWindowBounds_UseInclusiveStartOnly()
    {
        var ledgers = new ExperimentHostLedgers([
            Gate(Start, 300, "COMPLETED"), Gate(Start.AddTicks(1), 299.9),
            Gate(Start.AddTicks(-1), 400), Gate(AsOf, 500),
            Gate(Start.AddHours(1), 600, "failed"), Gate(Start.AddHours(2), 700, "cancelled")], []);

        Assert.Equal(1, ledgers.CompletedGateCount(Start, AsOf, 300));
        Assert.Equal(300d, ledgers.GateSeconds(Start, AsOf, 300));
        Assert.Equal(0, ledgers.CompletedGateCount(AsOf, AsOf, 300));
        Assert.Null(ledgers.GateSeconds(AsOf, AsOf, 300));
        Assert.Equal(0, ExperimentHostLedgers.Empty.CompletedGateCount(Start, AsOf));
        Assert.Null(ExperimentHostLedgers.Empty.GateSeconds(Start, AsOf));
    }

    [Fact]
    public void Count_OmittedMinimum_IncludesShortCompletedRunOnly()
    {
        var spec = GateSpec() with { Baseline = GateSpec().Baseline with { MinGateSeconds = null } };
        var record = Record(spec);
        var ledgers = new ExperimentHostLedgers(ComparisonRows(), []);

        Assert.Equal(5, ExperimentGateStopCount.Count(record, AsOf, ledgers,
            () => throw new InvalidOperationException("Ledger count must not read conduct attempts.")));
        Assert.Equal(5, ledgers.CompletedGateCount(Start, AsOf));
    }

    [Fact]
    public void GateSeconds_QualifyingPopulation_PreservesMedianAndCount()
    {
        var ledgers = new ExperimentHostLedgers([
            Gate(Start, 400), Gate(Start.AddHours(1), 500),
            Gate(Start.AddHours(2), 600), Gate(Start.AddHours(3), 100)], []);

        Assert.Equal(3, ledgers.CompletedGateCount(Start, AsOf, 300));
        Assert.Equal(500d, ledgers.GateSeconds(Start, AsOf, 300));
    }

    [Theory]
    [InlineData(false, 12)]
    [InlineData(true, 4)]
    public void Show_MetricSourceControl_UsesConductOrGuardrailLedger(bool gateGuardrail, int expected)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = CliCommandTestsExperiments.Spec() with
            {
                StopRule = new(4, ExperimentStopUnit.Gates),
                Baseline = GateSpec().Baseline,
                Guardrail = gateGuardrail
                    ? new("gate-seconds", new("gate-seconds", ">", 1000))
                    : CliCommandTestsExperiments.Spec().Guardrail
            };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            WriteLedgers(workspace, ComparisonRows());
            WriteAttempts(workspace);
            Assert.Equal(12, ConductCount(workspace, record));
            var reads = 0;
            var count = ExperimentGateStopCount.Count(record, AsOf,
                ExperimentHostLedgers.Read(workspace.ExecutionDirectory), () =>
                {
                    reads++;
                    return ExperimentGateAttempts.Read(workspace.ConductEventsLogPath);
                });

            Assert.Equal(expected, count);
            Assert.Equal(gateGuardrail ? 0 : 1, reads);
            if (!gateGuardrail) Assert.Equal(ConductCount(workspace, record), count);
            Assert.Contains($"stop rule: {expected} of 4 gates (met)", Show(workspace, record.Id));
        });
    }

    [Theory]
    [InlineData("GATE-SECONDS", null, true)]
    [InlineData(null, "GATE-SECONDS", true)]
    [InlineData("prefix-gate-seconds", "gate-seconds-suffix", false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    public void Count_ExactMetricMatchAndAbsentInputs_SelectSourceLazily(
        string? metric, string? guardrail, bool ledgerExpected)
    {
        var spec = GateSpec() with
        {
            Metrics = metric is null ? null! : [metric],
            Guardrail = guardrail is null ? null! : new(guardrail, new(guardrail, ">", 1000))
        };
        var attempts = new[] { Start, AsOf, Start.AddTicks(-1), AsOf.AddTicks(1) };
        var reads = 0;
        var count = ExperimentGateStopCount.Count(Record(spec), AsOf,
            new ExperimentHostLedgers(ComparisonRows(), []), () => { reads++; return attempts; });

        Assert.Equal(ledgerExpected ? 4 : 2, count);
        Assert.Equal(ledgerExpected ? 0 : 1, reads);
        if (!ledgerExpected) Assert.Equal(ExperimentGateAttempts.Count(attempts, Start, AsOf), count);
    }

    [Fact]
    public void Count_EmptyMetricsAndGuardrail_UsesInclusiveConductCount()
    {
        var spec = GateSpec() with { Metrics = [], Guardrail = null! };
        var attempts = new[] { Start, AsOf };
        Assert.Equal(2, ExperimentGateStopCount.Count(Record(spec), AsOf,
            new ExperimentHostLedgers(ComparisonRows(), []), () => attempts));
    }

    private static ExperimentSpec GateSpec() => new(
        "Full gate runs satisfy the gate-seconds stop rule",
        new(ExperimentInterventionKind.Policy, "Change manifest gate width"),
        new(ExperimentBaselineKind.BeforeAfterWindow, Start.AddDays(-1), Start, MinGateSeconds: 300),
        ["gate-seconds"], new("tick-cpu-ms", new("tick-cpu-ms", ">", 50)),
        new(4, ExperimentStopUnit.Gates),
        new([new("gate-seconds", "<=", -5)], [new("gate-seconds", ">=", 15)]));

    private static ExperimentRecord Record(ExperimentSpec spec) =>
        new("fixture", spec, Start.AddHours(1), ExperimentOutcomeState.Open, null);

    private static GateLaneTimeLedgerRecord[] ComparisonRows() => [
        Gate(Start.AddHours(1), 900), Gate(Start.AddHours(2), 1000),
        Gate(Start.AddHours(3), 1050), Gate(Start.AddHours(4), 1100),
        Gate(Start.AddHours(5), 100), Gate(Start.AddHours(6), 1200, "failed"),
        Gate(Start.AddHours(7), 800, "cancelled"), Gate(Start.AddTicks(-1), 1000), Gate(AsOf, 1000)];

    private static GateLaneTimeLedgerRecord Gate(DateTimeOffset at, double seconds, string outcome = "completed") =>
        new(at, $"fixture-{at:O}-{seconds}", "fixture-goal", outcome, seconds * 1000, seconds * 1000, 0, 0);

    private static void WriteLedgers(OrchestratorWorkspace workspace, IReadOnlyList<GateLaneTimeLedgerRecord> rows)
    {
        Write(GateLaneTimeLedger.ResolveStorePath(workspace.ExecutionDirectory),
            rows.Select(row => JsonSerializer.Serialize(row, JsonOptions)));
    }

    private static void WriteAttempts(OrchestratorWorkspace workspace) => Write(workspace.ConductEventsLogPath,
        Enumerable.Range(0, 12).Select(index => JsonSerializer.Serialize(new
        {
            timestamp = Start.AddMinutes(index + 1),
            eventKind = index % 3 == 2 ? "acceptance-cohort" : "acceptance",
            goalId = $"goal-{index}",
            detail = index % 3 == 2
                ? $"ACCEPTANCE_COHORT_CHILD_COMPLETED kind=acceptance attempt=child-{index} members=a+b verdict=failed"
                : $"ACCEPTANCE result={(index % 3 == 0 ? "passed" : "failed")} attempt=solo-{index}"
        })));

    private static void Write(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private static int ConductCount(OrchestratorWorkspace workspace, ExperimentRecord record) =>
        ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(workspace.ConductEventsLogPath),
            ExperimentReading.ComparisonStart(record), AsOf);

    private static JsonElement[] ReadingDueEvents(OrchestratorWorkspace workspace) =>
        File.ReadAllLines(workspace.ConductEventsLogPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(entry => entry.GetProperty("eventKind").GetString() == "experiment-reading-due").ToArray();

    private static IReadOnlyList<CollaborationItem> Items(OrchestratorWorkspace workspace) =>
        CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync().GetAwaiter().GetResult();

    private static string Show(OrchestratorWorkspace workspace, string id) =>
        CliCommandTestsExperiments.Execute(["experiment-show", id, "--as-of", AsOf.ToString("O")], workspace);
}
