using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its database, ledger root and AsyncLocal console capture.
public sealed class CliCommandTestsExperimentGateAudit
{
    private static readonly DateTimeOffset Since = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = Since.AddDays(1);
    private static readonly DateTimeOffset AsOf = Since.AddDays(2);

    [Fact]
    public void Show_AppendsGateAuditAfterReadingAndOverlapsWithStopRuleCount()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = CliCommandTestsExperiments.Spec() with
            {
                Baseline = new(ExperimentBaselineKind.AlternatingGates, Since, Until), Metrics = ["gate-seconds"],
                Guardrail = new("tick-cpu-ms", new("tick-cpu-ms", ">", 10)),
                DecisionRule = new([new("gate-seconds", "<", -10)], [new("gate-seconds", ">", 10)]),
                StopRule = new(9, ExperimentStopUnit.Gates)
            };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var path = GateLaneTimeLedger.ResolveStorePath(workspace.ExecutionDirectory);
            var off = new[] { 1400d, 1500, 1300, 1600, 1700 }.Select((seconds, i) => Gate(record.Id, "off", i, seconds)).ToArray();
            var on = new[] { 1000d, 1100, 1200, 1300 }.Select((seconds, i) => Gate(record.Id, "on", i, seconds)).ToArray();
            foreach (var row in on.Reverse().Concat(off.Reverse())) GateLaneTimeLedger.Append(path, row);
            var output = CliCommandTestsExperiments.Execute(["experiment-show", record.Id, "--as-of", AsOf.ToString("O")], workspace);
            const string metric = "gate seconds: baseline=1500 comparison=1150";
            Assert.Contains(metric, output);
            Assert.Contains("overlaps: none", output);
            Assert.Contains("gate audit:", output);
            Assert.True(output.IndexOf(metric, StringComparison.Ordinal) < output.IndexOf("overlaps: none", StringComparison.Ordinal));
            Assert.True(output.IndexOf("overlaps: none", StringComparison.Ordinal) < output.IndexOf("gate audit:", StringComparison.Ordinal));
            var expectedAudit = new[] { "gate audit:", "  arm off: 5 gates, median 1500s" }.Concat(off.Select(GateLine))
                .Append("  arm on: 4 gates, median 1150s").Concat(on.Select(GateLine));
            Assert.EndsWith(string.Join(Environment.NewLine, expectedAudit) + Environment.NewLine, output);
            var auditLines = output[output.IndexOf("gate audit:", StringComparison.Ordinal)..].Split(Environment.NewLine);
            var onStart = Array.FindIndex(auditLines, line => line.StartsWith("  arm on:", StringComparison.Ordinal));
            var offCount = auditLines.Take(onStart).Count(line => line.StartsWith("    off-", StringComparison.Ordinal));
            var onCount = auditLines.Skip(onStart).Count(line => line.StartsWith("    on-", StringComparison.Ordinal));
            Assert.Equal(5, offCount);
            Assert.Equal(4, onCount);
            Assert.Contains($"stop rule: {offCount + onCount} of 9 gates (met)", output);
        });
    }

    [Fact]
    public void Show_WithoutGateSecondsHasNoAuditOrExclusions()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = CliCommandTestsExperiments.Spec() with { Metrics = ["rounds-per-landing"],
                Guardrail = new("wasted-rounds", new("wasted-rounds", ">", 10)) };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            GateLaneTimeLedger.Append(GateLaneTimeLedger.ResolveStorePath(workspace.ExecutionDirectory),
                Gate(record.Id, "on", 0, 10) with { Outcome = "failed" });
            var output = CliCommandTestsExperiments.Execute(["experiment-show", record.Id, "--as-of", AsOf.ToString("O")], workspace);
            Assert.Contains("rounds per landing: baseline=", output);
            Assert.DoesNotContain("gate audit:", output);
            Assert.DoesNotContain("excluded", output);
        });
    }

    private static GateLaneTimeLedgerRecord Gate(string experimentId, string arm, int i, double seconds) =>
        new(Until.AddHours(i), $"{arm}-{i}", "fixture-goal", "completed", seconds * 1000, seconds * 1000, 0, 0,
            ExperimentArms: new Dictionary<string, string> { [experimentId] = arm });

    private static string GateLine(GateLaneTimeLedgerRecord row) =>
        $"    {row.GateAttemptId} {row.ObservedAt.ToString("O", CultureInfo.InvariantCulture)} {((row.TotalMs / 1000).ToString("G17", CultureInfo.InvariantCulture))}s";
}
