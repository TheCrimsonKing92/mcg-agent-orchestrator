using System.ComponentModel;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProcessProbeAccessDeniedTests
{
    [Fact]
    public void Liveness_AccessDenied_ReturnsNotRunning()
    {
        var reads = 0;
        bool ReadHasExited(int pid)
        {
            Assert.Equal(414, pid);
            reads++;
            throw new Win32Exception(5);
        }

        Assert.False(DispatchProcessRecoveryService.IsStillRunning(414, ReadHasExited));
        Assert.False(WorkerProcessJobs.IsProcessRunning(414, ReadHasExited));
        Assert.False(BacklogIntakeRecordStore.IsProcessAlive(414, ReadHasExited));
        Assert.Empty(new SystemTrialProcessInventory(ReadHasExited).FindSurvivors([414]));
        Assert.Equal(4, reads);
    }

    [Fact]
    public void Kill_AccessDenied_ReturnsGoneWithoutTaskkill()
    {
        var reads = 0;
        var taskkillCalls = 0;

        var gone = WorkerProcessJobs.DefaultTryKillPidTree(414, pid =>
        {
            Assert.Equal(414, pid);
            reads++;
            throw new Win32Exception(5);
        }, _ =>
        {
            taskkillCalls++;
            return false;
        });

        Assert.True(gone);
        Assert.Equal(1, reads);
        Assert.Equal(0, taskkillCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Liveness_ReadableProcess_PreservesExitState(bool hasExited)
    {
        Assert.Equal(!hasExited,
            DispatchProcessRecoveryService.IsStillRunning(414, _ => hasExited));
        Assert.Equal(!hasExited, WorkerProcessJobs.IsProcessRunning(414, _ => hasExited));
        Assert.Equal(!hasExited, BacklogIntakeRecordStore.IsProcessAlive(414, _ => hasExited));
        var survivors = new SystemTrialProcessInventory(_ => hasExited).FindSurvivors([414, 414]);
        Assert.Equal(hasExited ? Array.Empty<int>() : [414], survivors);
    }

    [Fact]
    public void Kill_OpenableWindowsProcess_InvokesTaskkill()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var taskkillCalls = 0;
        Assert.False(WorkerProcessJobs.DefaultTryKillPidTree(414, _ => false, pid =>
        {
            Assert.Equal(414, pid);
            taskkillCalls++;
            return false;
        }));
        Assert.Equal(1, taskkillCalls);
    }
}
