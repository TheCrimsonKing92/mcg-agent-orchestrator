using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every ledger round-trip owns and removes its temporary root.
public sealed class ExperimentGateAuditTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = Since.AddDays(1);
    private static readonly DateTimeOffset AsOf = Since.AddDays(2);

    [Fact]
    public void AlternatingArmsListEveryCountedGateAndMatchReadingMedians()
    {
        var record = Record(ExperimentBaselineKind.AlternatingGates);
        var off = new[] { 1400d, 1500, 1300, 1600, 1700 }.Select((seconds, i) =>
            Gate($"off-{i}", Until.AddHours(i), seconds, "off")).ToArray();
        var on = new[] { 1000d, 1100, 1200, 1300 }.Select((seconds, i) =>
            Gate($"on-{i}", Until.AddHours(i), seconds, "on")).ToArray();
        using var fixture = new LedgerFixture(on.Reverse().Concat(off.Reverse()));
        var reading = Evaluate(record, fixture.Ledgers);
        var metric = reading.Metrics.First(value => value.Metric == "gate-seconds");
        Assert.Equal(1500d, metric.Before);
        Assert.Equal(1150d, metric.After);
        Assert.Equal(new[] { "gate audit:", $"  arm off: 5 gates, median {Seconds(metric.Before!.Value)}s" }
            .Concat(off.Select(GateLine))
            .Append($"  arm on: 4 gates, median {Seconds(metric.After!.Value)}s")
            .Concat(on.Select(GateLine)), ExperimentGateAudit.Lines(record, fixture.Ledgers, AsOf));
    }

    [Fact]
    public void AlternatingExclusionsAreAttributedOnceAndRespectWindowBounds()
    {
        var record = Record(ExperimentBaselineKind.AlternatingGates);
        using var fixture = new LedgerFixture([
            Gate("failed-on", Until, 10, "on") with { Outcome = "failed" },
            Gate("unarmed", Until.AddHours(1), 10),
            Gate("other", Until, 10, "on") with { ExperimentArms = new Dictionary<string, string> { ["other-experiment"] = "on" } },
            Gate("before", Until.AddTicks(-1), 10, "off"),
            Gate("at-end", AsOf, 10, "off"),
            Gate("unknown-arm", Until.AddHours(2), 10, "unknown"),
            Gate("failed-unarmed", Until.AddHours(3), 10) with { Outcome = "failed" },
            Gate("failed-off", Until.AddHours(4), 10, "off") with { Outcome = "failed" }
        ]);
        Assert.Equal(new[] {
            "gate audit:", "  arm off: 0 gates, median none", "    excluded failed-off outcome-failed",
            "  arm on: 0 gates, median none", "    excluded failed-on outcome-failed",
            "    excluded unarmed no-arm-recorded", "    excluded unknown-arm no-arm-recorded",
            "    excluded failed-unarmed outcome-failed"
        }, ExperimentGateAudit.Lines(record, fixture.Ledgers, AsOf));
    }

    [Fact]
    public void BeforeAfterThresholdListsDroppedGateAndMatchesReadingMedians()
    {
        var record = Record(ExperimentBaselineKind.BeforeAfterWindow);
        record = record with { Spec = record.Spec with { Baseline = record.Spec.Baseline with { MinGateSeconds = 600 } } };
        var baseline = new[] { 400d, 700, 900 }.Select((seconds, i) => Gate($"baseline-{i}", Since.AddHours(i), seconds)).ToArray();
        var comparison = new[] { 650d, 800, 1000 }.Select((seconds, i) => Gate($"comparison-{i}", Until.AddHours(i), seconds)).ToArray();
        using var fixture = new LedgerFixture(baseline.Concat(comparison).Reverse());
        var metric = Evaluate(record, fixture.Ledgers).Metrics.First(value => value.Metric == "gate-seconds");
        Assert.Equal(800d, metric.Before);
        Assert.Equal(800d, metric.After);
        Assert.Equal(new[] { "gate audit:", $"  baseline window: 2 gates, median {Seconds(metric.Before!.Value)}s",
            GateLine(baseline[1]), GateLine(baseline[2]), "    excluded baseline-0 below-min-gate-seconds",
            $"  comparison window: 3 gates, median {Seconds(metric.After!.Value)}s" }.Concat(comparison.Select(GateLine)),
            ExperimentGateAudit.Lines(record, fixture.Ledgers, AsOf));
    }

    [Fact]
    public void BeforeAfterExclusionsPreferOutcomeAndUseHalfOpenWindows()
    {
        var record = Record(ExperimentBaselineKind.BeforeAfterWindow);
        record = record with { Spec = record.Spec with { Baseline = record.Spec.Baseline with { MinGateSeconds = 600 } } };
        using var fixture = new LedgerFixture([
            Gate("before", Since.AddTicks(-1), 700), Gate("baseline", Since, 600),
            Gate("failed", Since.AddHours(1), 400) with { Outcome = "failed" },
            Gate("comparison", Until, 600), Gate("at-end", AsOf, 700)
        ]);
        Assert.Equal(new[] { "gate audit:", "  baseline window: 1 gates, median 600s",
            GateLine(Gate("baseline", Since, 600)), "    excluded failed outcome-failed",
            "  comparison window: 1 gates, median 600s", GateLine(Gate("comparison", Until, 600)) },
            ExperimentGateAudit.Lines(record, fixture.Ledgers, AsOf));
    }

    [Fact]
    public void WithoutGateSecondsMetricOrGuardrailAuditIsEmpty()
    {
        var record = Record(ExperimentBaselineKind.BeforeAfterWindow);
        record = record with { Spec = record.Spec with { Metrics = ["rounds-per-landing"],
            Guardrail = new("wasted-rounds", new("wasted-rounds", ">", 10)) } };
        Assert.Empty(ExperimentGateAudit.Lines(record, ExperimentHostLedgers.Empty, AsOf));
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.TwinGoal)]
    [InlineData(ExperimentBaselineKind.GoalCohort)]
    public void GoalBaselinesSuppressGateAudit(ExperimentBaselineKind kind) =>
        Assert.Empty(ExperimentGateAudit.Lines(Record(kind), ExperimentHostLedgers.Empty, AsOf));

    [Fact]
    public void GuardrailAloneDeclaresAuditAndEmptyGroupsHaveNoMedian()
    {
        var record = Record(ExperimentBaselineKind.BeforeAfterWindow);
        record = record with { Spec = record.Spec with { Metrics = ["rounds-per-landing"],
            Guardrail = new("gate-seconds", new("gate-seconds", ">", 10)) } };
        Assert.Equal(new[] { "gate audit:", "  baseline window: 0 gates, median none",
            "  comparison window: 0 gates, median none" }, ExperimentGateAudit.Lines(record, ExperimentHostLedgers.Empty, AsOf));
    }

    [Fact]
    public void EqualTimestampsUseGateIdTieBreakAndInvariantSeconds()
    {
        var record = Record(ExperimentBaselineKind.AlternatingGates);
        using var fixture = new LedgerFixture([
            Gate("z", Until, 1.25, "on"), Gate("a", Until, 1.5, "on"),
            Gate("unarmed-z", Until, 10), Gate("unarmed-a", Until, 10)
        ]);
        Assert.Equal(new[] { "gate audit:", "  arm off: 0 gates, median none", "  arm on: 2 gates, median 1.375s",
            GateLine(Gate("a", Until, 1.5, "on")), GateLine(Gate("z", Until, 1.25, "on")),
            "    excluded unarmed-a no-arm-recorded", "    excluded unarmed-z no-arm-recorded" },
            ExperimentGateAudit.Lines(record, fixture.Ledgers, AsOf));
        Assert.All(Evaluate(record, fixture.Ledgers).Metrics, metric => { Assert.Null(metric.Before); Assert.Null(metric.After); });
    }

    private static ExperimentRecord Record(ExperimentBaselineKind kind) => new("audit-experiment",
        new("Gate audit fixture", new(ExperimentInterventionKind.BriefOrPromptChange, "fixture"),
            new(kind, Since, Until), ["gate-seconds"], new("tick-cpu-ms", new("tick-cpu-ms", ">", 10)),
            new(9, ExperimentStopUnit.Gates), new([new("gate-seconds", "<", -10)], [new("gate-seconds", ">", 10)])),
        Since, ExperimentOutcomeState.Open, null);

    private static ExperimentReadingResult Evaluate(ExperimentRecord record, ExperimentHostLedgers ledgers) =>
        ExperimentReading.Evaluate(record, [], new Dictionary<string, DateTimeOffset>(), [], AsOf, null, ledgers);

    private static GateLaneTimeLedgerRecord Gate(string id, DateTimeOffset at, double seconds, string? arm = null) =>
        new(at, id, "fixture-goal", "completed", seconds * 1000, seconds * 1000, 0, 0,
            ExperimentArms: arm is null ? null : new Dictionary<string, string> { ["audit-experiment"] = arm });

    private static string Seconds(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string GateLine(GateLaneTimeLedgerRecord row) =>
        $"    {row.GateAttemptId} {row.ObservedAt.ToString("O", CultureInfo.InvariantCulture)} {Seconds(row.TotalMs / 1000)}s";

    private sealed class LedgerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"gate-audit-{Guid.NewGuid():N}");
        internal ExperimentHostLedgers Ledgers { get; }

        internal LedgerFixture(IEnumerable<GateLaneTimeLedgerRecord> rows)
        {
            var path = GateLaneTimeLedger.ResolveStorePath(_root);
            foreach (var row in rows) GateLaneTimeLedger.Append(path, row);
            Ledgers = ExperimentHostLedgers.Read(_root);
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
