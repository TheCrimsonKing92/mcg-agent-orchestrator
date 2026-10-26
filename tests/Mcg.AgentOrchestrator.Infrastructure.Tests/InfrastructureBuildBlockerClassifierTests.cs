using Mcg.AgentOrchestrator.Core;
using static ConductorDriverTests;

// In-memory kernels and typed observations only; no filesystem or child processes.
public sealed class InfrastructureBuildBlockerClassifierTests
{
    [Theory]
    [InlineData(WorkerBuildOutcome.CapacityBeforeStart, WorkerBuildInfrastructureBlocker.QueueDeadline)]
    [InlineData(WorkerBuildOutcome.Interrupted, WorkerBuildInfrastructureBlocker.BuildApparatus)]
    [InlineData(WorkerBuildOutcome.ApparatusFailure, WorkerBuildInfrastructureBlocker.BuildApparatus)]
    [InlineData(WorkerBuildOutcome.TimeoutAfterStart, WorkerBuildInfrastructureBlocker.CompileTimeout)]
    public void UnconsumedCurrentInfrastructureIsClassified(WorkerBuildOutcome outcome, WorkerBuildInfrastructureBlocker expected)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        var invocation = RecordInvocation(kernel, goal, task, outcome);

        Assert.False(invocation.Consumed);
        Assert.Equal(expected, WorkerBuildInfrastructureEvidence.Classify(task));
        Assert.Equal(expected == WorkerBuildInfrastructureBlocker.QueueDeadline,
            WorkerBuildInfrastructureEvidence.LastDispatchEndedOnQueueCapacity(task));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 20)]
    [InlineData(true, 20)]
    public void AnyCompilerFailureVetoesQueueClassification(bool consumed, int minutes)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CompilerFailure, minutes: minutes, consumed: consumed);

        Assert.Equal(WorkerBuildInfrastructureBlocker.None, WorkerBuildInfrastructureEvidence.Classify(task));
        Assert.False(WorkerBuildInfrastructureEvidence.LastDispatchEndedOnQueueCapacity(task));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutstandingInvocationVetoesInfrastructure(bool terminal)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart);
        RecordInvocation(kernel, goal, task, terminal ? WorkerBuildOutcome.Interrupted : null, custodyReleased: false);

        Assert.Equal(WorkerBuildInfrastructureBlocker.None, WorkerBuildInfrastructureEvidence.Classify(task));
    }

    [Fact]
    public void EarlierDispatchInfrastructureDoesNotClassifyCurrentDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart);
        FinishAndRetry(kernel, goal, task);
        DispatchTask(kernel, goal, task);

        Assert.Equal(WorkerBuildInfrastructureBlocker.None, WorkerBuildInfrastructureEvidence.Classify(task));
        Assert.False(WorkerBuildInfrastructureEvidence.LastDispatchEndedOnQueueCapacity(task));
    }

    [Fact]
    public void MissingDispatchAndTextOnlyEvidenceDoNotClassify()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        Assert.Equal(WorkerBuildInfrastructureBlocker.None, WorkerBuildInfrastructureEvidence.Classify(task));
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "test.exe", "C:\\tmp", 1, "outcome=CapacityBeforeStart", string.Empty, DateTimeOffset.UtcNow));

        Assert.Empty(task.WorkerBuildInvocations);
        Assert.Equal(WorkerBuildInfrastructureBlocker.None, WorkerBuildInfrastructureEvidence.Classify(task));
        Assert.Equal(0, WorkerBuildInfrastructureEvidence.QueueCapacityDispatchCount(task));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LatestDeadlineSuccessOverridesEarlierQueueRegardlessOfRecordingOrder(bool successRecordedLast)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        if (!successRecordedLast) RecordInvocation(kernel, goal, task, WorkerBuildOutcome.Success, minutes: 20);
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart);
        if (successRecordedLast) RecordInvocation(kernel, goal, task, WorkerBuildOutcome.Success, minutes: 20);

        Assert.Equal(WorkerBuildInfrastructureBlocker.None, WorkerBuildInfrastructureEvidence.Classify(task));
    }

    [Theory]
    [InlineData(WorkerBuildOutcome.ApparatusFailure, true)]
    [InlineData(WorkerBuildOutcome.Cancelled, false)]
    [InlineData(WorkerBuildOutcome.StaleCandidate, false)]
    public void OtherTerminalOutcomesAreNotInfrastructure(WorkerBuildOutcome outcome, bool compilerStarted)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        RecordInvocation(kernel, goal, task, outcome, compilerStarted: compilerStarted);

        Assert.Equal(WorkerBuildInfrastructureBlocker.None, WorkerBuildInfrastructureEvidence.Classify(task));
    }

    [Fact]
    public void QueueCountIncludesUnconsumedDistinctDispatchesAndAgreesWhenConsumed()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        for (var round = 0; round < 2; round++)
        {
            DispatchTask(kernel, goal, task);
            RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart);
            RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart, minutes: 20);
            if (round == 0) FinishAndRetry(kernel, goal, task);
        }
        Assert.Equal(2, WorkerBuildInfrastructureEvidence.QueueCapacityDispatchCount(task));
        Assert.Equal(0, WorkerBuildCapacityEvidence.CapacityDispatchCount(task));
        foreach (var invocation in task.WorkerBuildInvocations.ToArray())
            kernel.RecordWorkerBuildInvocation(goal.Id, task.Id, invocation with { Version = invocation.Version + 1, Consumed = true });
        Assert.Equal(2, WorkerBuildInfrastructureEvidence.QueueCapacityDispatchCount(task));
        Assert.Equal(WorkerBuildCapacityEvidence.CapacityDispatchCount(task), WorkerBuildInfrastructureEvidence.QueueCapacityDispatchCount(task));
        RecordInvocation(kernel, goal, task, WorkerBuildOutcome.CapacityBeforeStart, custodyReleased: false);
        Assert.Equal(2, WorkerBuildInfrastructureEvidence.QueueCapacityDispatchCount(task));
    }

    private static void FinishAndRetry(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "test.exe", "C:\\tmp", 1, string.Empty, string.Empty, DateTimeOffset.UtcNow));
        kernel.RetryTaskAfterWorkerBuildCheckRecovery(goal.Id, task.Id, "next dispatch");
    }

    internal static WorkerBuildInvocation RecordInvocation(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        WorkerBuildOutcome? outcome, int minutes = 12, bool consumed = false, bool custodyReleased = true, bool compilerStarted = false)
    {
        var at = task.LastDispatch!.DispatchedAt;
        var id = Guid.NewGuid().ToString("N");
        var process = new WorkerBuildProcess(123, at, "test.exe");
        var requested = new WorkerBuildObservation(WorkerBuildPhase.Requested, at);
        var invocation = new WorkerBuildInvocation(id, id, goal.Id.Value, task.Id.Value, at, at.ToString("O"),
            task.LastDispatch.WorkingDirectory, "tree", "scope", ["App.csproj"], "Debug", at.AddMinutes(minutes),
            process, process, "admission.db", 1, 1, [requested]);
        kernel.RecordWorkerBuildInvocation(goal.Id, task.Id, invocation);
        if (outcome is null) return invocation;
        var started = compilerStarted || outcome is WorkerBuildOutcome.CompilerFailure or WorkerBuildOutcome.TimeoutAfterStart or WorkerBuildOutcome.Success;
        invocation = invocation with
        {
            Version = 2, Outcome = outcome, CustodyReleased = custodyReleased, Consumed = consumed,
            CompilerExitCode = outcome == WorkerBuildOutcome.Success ? 0 : outcome == WorkerBuildOutcome.CompilerFailure ? 1 : null,
            Children = started ? [process] : [],
            Observations = started
                ? [requested, new(WorkerBuildPhase.CompilerStarted, at), new(WorkerBuildPhase.Completed, at)]
                : [requested, new(WorkerBuildPhase.WaitingForBuildSlot, at), new(WorkerBuildPhase.Completed, at)]
        };
        invocation.RequireValid();
        kernel.RecordWorkerBuildInvocation(goal.Id, task.Id, invocation);
        return invocation;
    }
}
