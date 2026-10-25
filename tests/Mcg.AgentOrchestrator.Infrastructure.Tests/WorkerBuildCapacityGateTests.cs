using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel safe: only private temporary files, injected locks and an in-memory kernel.
public sealed class WorkerBuildCapacityGateTests : IDisposable
{
    private readonly DotnetBuildStorageRoot _root = new(Path.Combine(Path.GetTempPath(), "capacity-gate-" + Guid.NewGuid().ToString("N")));
    private readonly RecordingTimeProvider _clock = new();

    [Theory]
    [InlineData(2, 1, (int)WorkerBuildCapacityVerdict.Hold)]
    [InlineData(1, 1, (int)WorkerBuildCapacityVerdict.Hold)]
    [InlineData(1, 0, (int)WorkerBuildCapacityVerdict.Retry)]
    public void Snapshot_PermitsAndLiveTickets_DecidesAdmission(
        int heldCount, int waiting, int expected)
    {
        var queue = Queue();
        for (var index = 0; index < waiting; index++) queue.Register(Claim("worker-" + index));
        var held = Enumerable.Range(0, heldCount).Select(index =>
            DotnetBuildEnvironmentManager.BuildSlotExecutionLockPath(index, _root)).ToHashSet(StringComparer.Ordinal);

        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(_root, _clock, held.Contains);

        Assert.Equal(new WorkerBuildCapacitySnapshot(2 - heldCount, 2, waiting, true), snapshot);
        Assert.Equal((WorkerBuildCapacityVerdict)expected, WorkerBuildCapacityGate.Decide(snapshot, 1));
        if (heldCount == 2)
            Assert.Equal("worker build capacity: waiting for a host build permit; unheld=0/2; waiting=1; capacity rounds=1/3",
                WorkerBuildCheckRecoveryText.CapacityHeldReason(snapshot, 1));
    }

    [Fact]
    public void Snapshot_StaleTicket_DoesNotConsumeFreeCapacity()
    {
        var queue = Queue();
        queue.Register(Claim("stale"));
        Assert.Single(queue.Snapshot());
        _clock.Advance(BuildPermitWaitQueue.TicketStaleAfter);
        var heldPath = DotnetBuildEnvironmentManager.BuildSlotExecutionLockPath(0, _root);

        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(_root, _clock, path => path == heldPath);

        Assert.Equal(new WorkerBuildCapacitySnapshot(1, 2, 0, true), snapshot);
        Assert.Equal(WorkerBuildCapacityVerdict.Retry, WorkerBuildCapacityGate.Decide(snapshot, 1));
    }

    [Fact]
    public void Decide_FourCapacityDispatches_ExhaustsBeforeReadabilityOrAvailability()
    {
        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(_root, _clock, _ => false);
        Assert.Equal(new WorkerBuildCapacitySnapshot(2, 2, 0, true), snapshot);
        Assert.Equal(WorkerBuildCapacityVerdict.Exhausted, WorkerBuildCapacityGate.Decide(snapshot, 4));
        Assert.Equal(WorkerBuildCapacityVerdict.Exhausted,
            WorkerBuildCapacityGate.Decide(WorkerBuildCapacitySnapshot.Unreadable, 4));
        Assert.Equal(WorkerBuildCapacityVerdict.Retry, WorkerBuildCapacityGate.Decide(snapshot, 3));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Snapshot_FreshCorruptTicket_IsUnreadableAndFailsOpen(string content)
    {
        var queue = Queue();
        queue.Register(Claim("corrupt"));
        var path = queue.TicketPath("corrupt");
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime);

        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(_root, _clock, _ => true);

        Assert.False(snapshot.Readable);
        Assert.Equal(WorkerBuildCapacityVerdict.Retry, WorkerBuildCapacityGate.Decide(snapshot, 1));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("current-capacity", true)]
    [InlineData("earlier-dispatch", false)]
    [InlineData("compiler-failure", false)]
    [InlineData("text-only", false)]
    public void LastDispatch_TypedConsumedEvidence_IsRequired(string scenario, bool expected)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        if (scenario != "text-only")
            RecordInvocation(kernel, goal, task, scenario == "compiler-failure"
                ? WorkerBuildOutcome.CompilerFailure : WorkerBuildOutcome.CapacityBeforeStart);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "test.exe", "C:\\tmp", 1, "outcome=CapacityBeforeStart", string.Empty, DateTimeOffset.UtcNow));
        if (scenario == "earlier-dispatch")
        {
            kernel.RetryTaskAfterWorkerBuildCheckRecovery(goal.Id, task.Id, "next dispatch");
            DispatchTask(kernel, goal, task);
        }

        Assert.Equal(expected, WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task));
    }

    [Fact]
    public void LastDispatch_DeadlineThenRecordingOrder_IgnoresUnconsumedEvidence()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        Assert.False(WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task));
        DispatchTask(kernel, goal, task);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart, minutes: 2);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CompilerFailure, minutes: 1);
        Assert.True(WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task));
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CompilerFailure, minutes: 3, consumed: false);
        Assert.True(WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task));
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CompilerFailure, minutes: 2);
        Assert.False(WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task));
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart, minutes: 2);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart, minutes: 4, consumed: false);
        Assert.True(WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task));
        Assert.Equal(1, WorkerBuildCapacityEvidence.CapacityDispatchCount(task));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("json")]
    public void Snapshot_ExpectedReadFailure_IsUnreadable(string failure)
    {
        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(_root, _clock, _ =>
        {
            if (failure == "io") throw new IOException("injected");
            if (failure == "access") throw new UnauthorizedAccessException("injected");
            throw new System.Text.Json.JsonException("injected");
        });
        Assert.False(snapshot.Readable);
        Assert.Equal(WorkerBuildCapacityVerdict.Retry, WorkerBuildCapacityGate.Decide(snapshot, 1));
    }

    internal static void RecordInvocation(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        WorkerBuildOutcome outcome, int minutes = 12, bool consumed = true)
    {
        var at = task.LastDispatch!.DispatchedAt;
        var id = Guid.NewGuid().ToString("N");
        var process = new WorkerBuildProcess(123, at, "test.exe");
        var requested = new WorkerBuildObservation(WorkerBuildPhase.Requested, at);
        var invocation = new WorkerBuildInvocation(id, id, goal.Id.Value, task.Id.Value, at, at.ToString("O"),
            task.LastDispatch.WorkingDirectory, "tree", "scope", ["App.csproj"], "Debug", at.AddMinutes(minutes),
            process, process, "admission.db", 1, 1, [requested]);
        kernel.RecordWorkerBuildInvocation(goal.Id, task.Id, invocation);
        var compiler = outcome == WorkerBuildOutcome.CompilerFailure;
        invocation = invocation with
        {
            Version = 2, Outcome = outcome, CustodyReleased = true,
            CompilerExitCode = compiler ? 1 : null,
            Children = compiler ? [process] : [],
            Observations = compiler
                ? [requested, new(WorkerBuildPhase.CompilerStarted, at), new(WorkerBuildPhase.Completed, at)]
                : [requested, new(WorkerBuildPhase.WaitingForBuildSlot, at), new(WorkerBuildPhase.Completed, at)]
        };
        kernel.RecordWorkerBuildInvocation(goal.Id, task.Id, invocation);
        if (consumed)
            kernel.RecordWorkerBuildInvocation(goal.Id, task.Id, invocation with { Version = 3, Consumed = true });
    }

    private BuildPermitWaitQueue Queue() => new(_root, _clock);
    private static BuildPermitClaimant Claim(string key) =>
        new(key, BuildPermitClaimantKind.WorkerBuild, Environment.ProcessId, OwnerBound: false);

    public void Dispose()
    {
        if (Directory.Exists(_root.RootPath)) Directory.Delete(_root.RootPath, recursive: true);
    }
}
