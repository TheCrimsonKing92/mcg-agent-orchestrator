using System.ComponentModel;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorWatchProgressReporterLivenessTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ThrowingCandidateDoesNotHideMatchingLiveWorker()
    {
        var (kernel, goal) = SimpleGoal("watch a live worker");
        var task = goal.Tasks.Single();
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "run", "C:\\repo", now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(303, "run", "C:\\repo", "worker.out.log", "worker.err.log",
                "worker.exit.txt", now, null, null, OwnedProcessIds: [101, 202]));
        var reporter = new ConductorWatchProgressReporter(
            readHeartbeat: (process, observedAt) => new DispatchHeartbeatStatus(
                "heartbeat.json", true, null, process.ProcessId, null, [101, 202], "running",
                observedAt, observedAt, TimeSpan.Zero, TimeSpan.Zero, 0, 0)
            {
                OwnedProcessIdentities = [Identity(101), Identity(202)]
            },
            readChanges: (_, _) => new DispatchLiveChangeSnapshot([], [], 0),
            isProcessAlive: pid => pid == 101 ? throw new Win32Exception(5) : pid == 202,
            readProcessIdentity: Identity,
            now: () => now.AddMinutes(1),
            loopProcessId: 9001,
            launcherProcessId: 9002);

        var lines = reporter.BuildLines(kernel.GetGoal(goal.Id), false, ConductorAutonomyPolicy.Conservative);

        var machine = Assert.Single(lines, line => line.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal));
        Assert.Contains("pid=202", machine);
        Assert.DoesNotContain("pid=101", machine);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DefaultProbeReturnsFalseWhenLookupCannotBeQueried(int kind)
    {
        Exception failure = kind switch
        {
            0 => new Win32Exception(5),
            1 => new InvalidOperationException(),
            2 => new ArgumentException(),
            _ => new NotSupportedException()
        };

        Assert.False(ConductorWatchProgressReporter.IsProcessAlive(101, _ => throw failure));
    }

    private static SpawnProcessIdentity Identity(int pid) =>
        new(pid, new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero), $@"C:\workers\{pid}.exe");
}
