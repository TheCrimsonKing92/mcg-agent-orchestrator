using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: each fixture owns its workspace and log; no background work or timing assertions.
public sealed class ConductorStatusSnapshotReaderSharingTests
{
    [Fact]
    public void LatestTick_ReadsConductLogWhileWriteHandleRemainsOpen()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        var tick = new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(workspace.LogDirectory);
        File.WriteAllText(workspace.ConductEventsLogPath,
            JsonSerializer.Serialize(new { timestamp = tick, eventKind = "tick", detail = "TICK tick=1" }) + "\n");
        Assert.False(File.Exists(workspace.RunEventStorePath));
        using var writer = new FileStream(workspace.ConductEventsLogPath, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(tick, ConductorStatusSnapshotReader.LatestTick(workspace));
        Assert.True(writer.CanWrite);
    }

    [Fact]
    public void LatestTick_MissingConductLogReturnsNullWithoutCreatingIt()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        Directory.CreateDirectory(workspace.LogDirectory);
        Assert.False(File.Exists(workspace.ConductEventsLogPath));
        Assert.False(File.Exists(workspace.RunEventStorePath));

        Assert.Null(ConductorStatusSnapshotReader.LatestTick(workspace));
        Assert.False(File.Exists(workspace.ConductEventsLogPath));
    }
}
