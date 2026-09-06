using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class StaleDispatchProcessReconcilerTests
{
    [Xunit.Fact]
    public void NativeExitCompletesStaleRunningRecord()
    {
        var fixture = CreateFixture(DispatchExitArtifacts.Native(0, "worker exited", FixedNow));
        try
        {
            var count = StaleDispatchProcessReconciler.Reconcile(
                fixture.Kernel,
                fixture.Goal,
                StaleDispatchProcessReconciler.AssignedOnly,
                _ => false,
                _ => null,
                () => FixedNow);

            Xunit.Assert.Equal(1, count);
            var process = Xunit.Assert.IsType<TaskProcessRecord>(fixture.Task.LastProcess);
            Xunit.Assert.False(process.IsRunning);
            Xunit.Assert.Equal(DispatchExitArtifactOrigin.Native, process.ExitArtifactOrigin);
            Xunit.Assert.False(process.WasCancelled);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SyntheticExitPreservesStaleRunningRecord()
    {
        var fixture = CreateFixture(DispatchExitArtifacts.Synthetic(1, "conductor interruption", FixedNow));
        try
        {
            var count = StaleDispatchProcessReconciler.Reconcile(
                fixture.Kernel,
                fixture.Goal,
                StaleDispatchProcessReconciler.AssignedOnly,
                _ => false,
                _ => null,
                () => FixedNow);

            Xunit.Assert.Equal(0, count);
            Xunit.Assert.True(fixture.Task.LastProcess is { IsRunning: true });
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void LiveOwnedProcessPreservesStaleRunningRecord()
    {
        var fixture = CreateFixture(DispatchExitArtifacts.Native(0, "host exited", FixedNow));
        var childIdentity = new SpawnProcessIdentity(28517, FixedNow.AddSeconds(1), @"C:\workers\child.exe");
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(fixture.Task.LastProcess!),
            "{\"pid\":28516,\"childPid\":28517,\"ownedPids\":[28517],\"ownedProcessIdentities\":[{\"processId\":28517,\"startedAt\":\"2026-09-05T15:48:01Z\",\"imagePath\":\"C:\\\\workers\\\\child.exe\"}],\"state\":\"running\",\"lastObservedAt\":\"2026-09-05T15:48:00Z\",\"lastProgressAt\":\"2026-09-05T15:48:00Z\",\"stdoutBytes\":4,\"stderrBytes\":0,\"ownedCpuMs\":1}");
        try
        {
            var count = StaleDispatchProcessReconciler.Reconcile(
                fixture.Kernel,
                fixture.Goal,
                StaleDispatchProcessReconciler.AssignedOnly,
                pid => pid == childIdentity.ProcessId,
                pid => pid == childIdentity.ProcessId ? childIdentity : null,
                () => FixedNow);

            Xunit.Assert.Equal(0, count);
            Xunit.Assert.True(fixture.Task.LastProcess is { IsRunning: true });
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-09-05T15:48:00Z");

    private static ProcessFixture CreateFixture(DispatchExitArtifact artifact)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-stale-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Reconcile stale process", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Reconcile a completed Reviewer round", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "exit.txt");
        File.WriteAllText(stdout, "done");
        File.WriteAllText(stderr, string.Empty);
        DispatchExitArtifacts.Write(exit, artifact);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("reviewer", "review", root, FixedNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(28516, "review", root, stdout, stderr, exit, FixedNow, null, null));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var assignedTask = snapshot.Tasks.Single() with { Status = WorkTaskStatus.Assigned };
        kernel.ReplaceGoalWithSnapshot(snapshot with { Tasks = [assignedTask] });
        goal = kernel.GetGoal(goal.Id);
        return new ProcessFixture(root, kernel, goal, goal.Tasks.Single());
    }

    private sealed record ProcessFixture(
        string Root,
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Task);
}
