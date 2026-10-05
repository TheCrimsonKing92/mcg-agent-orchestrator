using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalLifecycleOutboxTelemetryIsolationTests : CliGoalLifecycleOutboxTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("abandon", 0)]
    [Xunit.InlineData("stop-abandon", 0)]
    [Xunit.InlineData("abandon", 3000)]
    [Xunit.InlineData("stop-abandon", 3000)]
    public async Task FixedWriteDuration_ControlsLogArtifactsAndRetentionStdout(string command, int holdMs)
    {
        // The zero case exercises the fixture default; the slow case forces telemetry without sleeping.
        using var seed = await Seed.Create(command, GoalStatus.Cancelled,
            writeHoldDuration: holdMs == 0 ? null : TimeSpan.FromMilliseconds(holdMs));
        Xunit.Assert.False(DotnetBuildEnvironmentManager.InspectGoalLease(seed.GoalId).RootExists);

        var result = Transition(seed, command);

        Xunit.Assert.Null(result.Error);
        Xunit.Assert.True(result.Changed);
        var hasTelemetry = holdMs > 0;
        Xunit.Assert.Equal(hasTelemetry, Directory.Exists(seed.Workspace.LogDirectory));
        Xunit.Assert.Contains($"  WorkerLogs: Keep; exists={hasTelemetry}{Environment.NewLine}", result.Output);
        Xunit.Assert.False(File.Exists(seed.EventPath));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
        var diagnosticsPath = Path.Combine(seed.Workspace.LogDirectory, SqliteWriteTelemetry.DiagnosticsFileName);
        Xunit.Assert.Equal(hasTelemetry, File.Exists(diagnosticsPath));
        if (hasTelemetry)
        {
            var receipts = File.ReadAllLines(diagnosticsPath);
            Xunit.Assert.NotEmpty(receipts);
            foreach (var line in receipts)
            {
                using var receipt = JsonDocument.Parse(line);
                Xunit.Assert.Equal("sqlite-state-write-hold", receipt.RootElement.GetProperty("eventType").GetString());
                Xunit.Assert.Equal("critical", receipt.RootElement.GetProperty("severity").GetString());
                Xunit.Assert.Equal(holdMs, receipt.RootElement.GetProperty("holdMs").GetDouble());
            }
        }
    }
}
