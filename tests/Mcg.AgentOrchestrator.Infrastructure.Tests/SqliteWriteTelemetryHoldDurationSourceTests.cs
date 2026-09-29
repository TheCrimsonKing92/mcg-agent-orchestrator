using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SqliteWriteTelemetryHoldDurationSourceTests
{
    [Xunit.Fact]
    public void SuppliedHoldDurationEmitsExactCriticalCommittedReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlite-hold-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var diagnostics = Path.Combine(root, "holds.jsonl");
            var operations = new List<string>();
            var reads = 0;
            var options = new SqliteWriteTelemetryOptions
            {
                DiagnosticsPath = diagnostics,
                MirrorToConductEventStream = false,
                HoldDurationSource = operation =>
                {
                    operations.Add(operation);
                    return () => { reads++; return TimeSpan.FromSeconds(3); };
                }
            };
            var scope = new SqliteWriteTelemetry(Path.Combine(root, "state.db"), options)
                .StartScope("source-test", TimeSpan.Zero);
            Xunit.Assert.Equal("source-test", Xunit.Assert.Single(operations));
            Xunit.Assert.Equal(0, reads);

            scope.Emit("commit");

            Xunit.Assert.Equal(1, reads);
            var receipt = JsonNode.Parse(Xunit.Assert.Single(File.ReadAllLines(diagnostics)))!.AsObject();
            Xunit.Assert.Equal("sqlite-state-write-hold", receipt["eventType"]!.GetValue<string>());
            Xunit.Assert.Equal("critical", receipt["severity"]!.GetValue<string>());
            Xunit.Assert.Equal("commit", receipt["disposition"]!.GetValue<string>());
            Xunit.Assert.Equal(3_000d, receipt["holdMs"]!.GetValue<double>());
            Xunit.Assert.NotEmpty(receipt["stackSummary"]!.AsArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MissingSourceUsesDefaultStopwatchMeasurement()
    {
        Xunit.Assert.Null(new SqliteWriteTelemetryOptions().HoldDurationSource);
        Xunit.Assert.Null(SqliteWriteTelemetryOptions.FromEnvironment().HoldDurationSource);
        var root = Path.Combine(Path.GetTempPath(), "sqlite-default-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var diagnostics = Path.Combine(root, "holds.jsonl");
            var options = new SqliteWriteTelemetryOptions
            {
                DiagnosticsPath = diagnostics,
                MirrorToConductEventStream = false,
                WarningHoldThreshold = TimeSpan.FromTicks(-1),
                CriticalHoldThreshold = TimeSpan.MaxValue
            };
            new SqliteWriteTelemetry(Path.Combine(root, "state.db"), options)
                .StartScope("default-test", TimeSpan.Zero).Emit("commit");

            var receipt = JsonNode.Parse(Xunit.Assert.Single(File.ReadAllLines(diagnostics)))!.AsObject();
            Xunit.Assert.Equal("warning", receipt["severity"]!.GetValue<string>());
            Xunit.Assert.True(receipt["holdMs"]!.GetValue<double>() >= 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
