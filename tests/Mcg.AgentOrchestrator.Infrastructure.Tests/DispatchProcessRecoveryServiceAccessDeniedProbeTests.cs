using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchProcessRecoveryServiceAccessDeniedProbeTests
{
    [Fact]
    public void Reap_AccessDeniedAccounting_CompletesWithoutMetrics()
    {
        var process = ProcessRecord();
        var accessor = new DeniedProcessAccessor();
        var readProcessIds = new List<int>();
        var killedProcessIds = new List<int>();
        var service = CreateService(process, pid =>
        {
            readProcessIds.Add(pid);
            return DispatchProcessRecoveryService.ReadPeakMemoryBytes(
                pid, _ => accessor.HasExited ? null : 4096);
        }, killedProcessIds);

        Assert.Null(service.SnapshotTrackedProcessAccounting(process));
        Assert.Equal(process.TrackedProcessIds, readProcessIds);
        readProcessIds.Clear();

        Assert.Null(service.ReapTrackedProcessJobs(process, waitForExit: false));
        Assert.Equal(process.TrackedProcessIds, readProcessIds);
        Assert.Equal(process.TrackedProcessIds, killedProcessIds);
    }

    [Fact]
    public void Snapshot_ReadablePeak_PreservesLiveAccounting()
    {
        var process = ProcessRecord();
        var reads = 0;
        var service = CreateService(process, pid =>
            DispatchProcessRecoveryService.ReadPeakMemoryBytes(pid, _ =>
            {
                reads++;
                return 4096;
            }), []);

        var snapshot = Assert.IsType<TaskProcessResourceAccounting>(
            service.SnapshotTrackedProcessAccounting(process));

        Assert.Equal(1, reads);
        Assert.Equal(4096L, snapshot.PeakMemoryBytes);
        Assert.Equal("snapshot", snapshot.AccountingSource);
    }

    [Fact]
    public void Snapshot_AccessDeniedPeak_RetainsHeartbeatPeak()
    {
        var process = ProcessRecord();
        var reads = 0;
        var service = CreateService(process, pid =>
            DispatchProcessRecoveryService.ReadPeakMemoryBytes(pid, _ =>
            {
                reads++;
                throw new Win32Exception(5);
            }), [], heartbeatPeak: 8192);

        var snapshot = Assert.IsType<TaskProcessResourceAccounting>(
            service.SnapshotTrackedProcessAccounting(process));

        Assert.Equal(1, reads);
        Assert.Equal(8192L, snapshot.PeakMemoryBytes);
    }

    private static TaskProcessRecord ProcessRecord() => new(
        414, "worker command", "memory", "memory/job.stdout.log",
        "memory/job.stderr.log", "memory/job.exit.txt",
        new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero), null, null);

    private static DispatchProcessRecoveryService CreateService(
        TaskProcessRecord process, Func<int, long?> readPeak, List<int> killedProcessIds,
        long? heartbeatPeak = null)
    {
        var heartbeatPath = DispatchProcessRecoveryService.GetHeartbeatPath(process);
        var heartbeat = JsonSerializer.Serialize(new
        {
            pid = process.ProcessId,
            state = "running",
            lastObservedAt = process.StartedAt,
            lastProgressAt = process.StartedAt,
            stdoutBytes = 0,
            stderrBytes = 0,
            ownedCpuMs = (long?)null,
            ownedPeakMemoryBytes = heartbeatPeak,
            ownedIoBytes = (long?)null
        });
        return new DispatchProcessRecoveryService(
            isStillRunning: _ => false,
            tryKillOwnedProcess: pid =>
            {
                killedProcessIds.Add(pid);
                return true;
            },
            fileExists: path => path == heartbeatPath,
            openArtifactReadStream: path =>
            {
                Assert.Equal(heartbeatPath, path);
                return new MemoryStream(Encoding.UTF8.GetBytes(heartbeat));
            },
            getPeakMemoryBytes: readPeak);
    }

    private sealed class DeniedProcessAccessor
    {
        public bool HasExited => throw new Win32Exception(5);
    }
}
