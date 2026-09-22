using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalAcceptanceVerifierProcessSnapshotTests
{
    [Xunit.Fact]
    public void ExitedHeartbeatPidPreservesTransientNoHolderRetryEligibility()
    {
        var artifactsPath = Path.Combine(Path.GetTempPath(), "mcg-gate-artifacts");
        var observedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var heartbeat = new GateHeartbeatSnapshot(
            null,
            "test",
            "gate",
            null,
            101,
            null,
            "running",
            observedAt,
            observedAt,
            observedAt,
            0,
            0,
            0,
            $"dotnet test --artifacts-path {artifactsPath}");
        var environment = new DotnetBuildEnvironment(
            "lease",
            Path.GetTempPath(),
            artifactsPath,
            Path.Combine(Path.GetTempPath(), "gate.lock"),
            [],
            "owner");

        var holders = GateHeartbeatLockHolderProjection.Build(
            heartbeat,
            environment,
            _ => new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>
                {
                    [101] = new(
                        101,
                        1,
                        "dotnet",
                        null,
                        null,
                        null,
                        ProcessInspectionStatus.Exited)
                })).ToArray();

        Assert.Empty(holders);
    }

    [Xunit.Fact]
    public void HeartbeatHoldersReuseSnapshotIdentityAndFailClosed()
    {
        var artifactsPath = Path.Combine(Path.GetTempPath(), "mcg-gate-artifacts");
        var startedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var heartbeat = new GateHeartbeatSnapshot(
            null,
            "test",
            "gate",
            null,
            101,
            202,
            "running",
            startedAt,
            startedAt,
            startedAt,
            0,
            0,
            0,
            $"dotnet test --artifacts-path {artifactsPath}");
        var environment = new DotnetBuildEnvironment(
            "lease",
            Path.GetTempPath(),
            artifactsPath,
            Path.Combine(Path.GetTempPath(), "gate.lock"),
            [],
            "owner");
        var snapshotCalls = 0;

        var holders = GateHeartbeatLockHolderProjection.Build(
            heartbeat,
            environment,
            processIds =>
            {
                snapshotCalls++;
                Assert.Equal([101, 202], processIds);
                return new ProcessCommandLineSnapshot(
                    new Dictionary<int, ProcessInspectionRecord>
                    {
                        [101] = new(
                            101,
                            1,
                            "dotnet",
                            "C:\\Program Files\\dotnet\\dotnet.exe",
                            startedAt,
                            $"dotnet test --artifacts-path {artifactsPath}",
                            ProcessInspectionStatus.Available),
                        [202] = new(
                            202,
                            101,
                            "dotnet",
                            null,
                            null,
                            null,
                            ProcessInspectionStatus.AccessDenied)
                    });
            }).ToArray();

        Assert.Equal(1, snapshotCalls);
        var available = Assert.Single(holders, holder => holder.ProcessId == 101);
        Assert.Equal("dotnet", available.ProcessName);
        Assert.Equal(startedAt, available.ProcessStartTime);
        Assert.True(available.IsOrchestratorOwned);
        var unavailable = Assert.Single(holders, holder => holder.ProcessId == 202);
        Assert.Equal("process-inspection-AccessDenied", unavailable.ProcessName);
        Assert.False(unavailable.IsOrchestratorOwned);
    }
}
