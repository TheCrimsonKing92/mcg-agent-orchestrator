using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns a private temp root and supplies all telemetry options.
public sealed class SqliteWriteTelemetryDiagnosticsLocationTests
{
    [Xunit.Fact]
    public void DefaultPath_SlowCommit_WritesDiagnosticsWithoutLogs()
    {
        using var root = new TempRoot();
        var telemetry = new SqliteWriteTelemetry(Path.Combine(root.Path, "state.db"), new SqliteWriteTelemetryOptions
        {
            MirrorToConductEventStream = false,
            WarningHoldThreshold = TimeSpan.Zero,
            HoldDurationSource = _ => () => TimeSpan.FromSeconds(3)
        });

        telemetry.StartScope("location-test", TimeSpan.Zero).Emit("commit");

        var receipt = JsonNode.Parse(Xunit.Assert.Single(File.ReadAllLines(root.DiagnosticsPath)))!.AsObject();
        Xunit.Assert.Equal("sqlite-state-write-hold", receipt["eventType"]!.GetValue<string>());
        Xunit.Assert.Equal(3_000d, receipt["holdMs"]!.GetValue<double>());
        Xunit.Assert.False(Directory.Exists(Path.Combine(root.Path, "logs")));
    }

    [Xunit.Fact]
    public void DefaultPath_BusyAndFailureReceipts_WriteDiagnosticsWithoutLogs()
    {
        using var root = new TempRoot();
        var telemetry = new SqliteWriteTelemetry(Path.Combine(root.Path, "state.db"), new SqliteWriteTelemetryOptions
        {
            MirrorToConductEventStream = false
        });
        var busy = new SqliteException("busy", 5);

        telemetry.EmitBusyRetry("location-test", TimeSpan.Zero, busy, attemptCount: 1);
        telemetry.EmitBusyRecovered("location-test", TimeSpan.Zero, busy, attemptCount: 1);
        telemetry.EmitBusyFailure("location-test", TimeSpan.Zero, busy, attemptCount: 1);
        telemetry.EmitFailure("location-test", new InvalidOperationException("failed"), attemptCount: 1, disposition: "failed");

        Xunit.Assert.Equal(4, File.ReadAllLines(root.DiagnosticsPath).Length);
        Xunit.Assert.False(Directory.Exists(Path.Combine(root.Path, "logs")));
    }

    [Xunit.Fact]
    public void MirrorEnabled_SlowCommit_WritesDiagnosticsAndConductLog()
    {
        using var root = new TempRoot();
        var telemetry = new SqliteWriteTelemetry(Path.Combine(root.Path, "state.db"), new SqliteWriteTelemetryOptions
        {
            MirrorToConductEventStream = true,
            WarningHoldThreshold = TimeSpan.Zero,
            HoldDurationSource = _ => () => TimeSpan.FromSeconds(3)
        });

        telemetry.StartScope("location-test", TimeSpan.Zero).Emit("commit");

        Xunit.Assert.Single(File.ReadAllLines(root.DiagnosticsPath));
        var conductLine = Xunit.Assert.Single(File.ReadAllLines(Path.Combine(root.Path, "logs", "conduct-events.log")));
        Xunit.Assert.Contains("SQLITE_WRITE_TELEMETRY", conductLine);
    }

    [Xunit.Fact]
    public void ExplicitPath_SlowCommit_WritesOnlyToOverride()
    {
        using var root = new TempRoot();
        var diagnosticsPath = Path.Combine(root.Path, "explicit", "holds.jsonl");
        var telemetry = new SqliteWriteTelemetry(Path.Combine(root.Path, "state.db"), new SqliteWriteTelemetryOptions
        {
            DiagnosticsPath = diagnosticsPath,
            MirrorToConductEventStream = false,
            WarningHoldThreshold = TimeSpan.Zero,
            HoldDurationSource = _ => () => TimeSpan.FromSeconds(3)
        });

        telemetry.StartScope("location-test", TimeSpan.Zero).Emit("commit");

        Xunit.Assert.Single(File.ReadAllLines(diagnosticsPath));
        Xunit.Assert.False(Directory.Exists(Path.Combine(root.Path, SqliteWriteTelemetry.DiagnosticsDirectoryName)));
        Xunit.Assert.False(Directory.Exists(Path.Combine(root.Path, "logs")));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "sqlite-diagnostics-location-" + Guid.NewGuid().ToString("N"));

        public string DiagnosticsPath => System.IO.Path.Combine(Path,
            SqliteWriteTelemetry.DiagnosticsDirectoryName, SqliteWriteTelemetry.DiagnosticsFileName);

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
