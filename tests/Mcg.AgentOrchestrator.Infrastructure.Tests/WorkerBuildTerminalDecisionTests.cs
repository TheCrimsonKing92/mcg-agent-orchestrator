using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildTerminalDecision;

// Parallel-safe: literal observations, controlled timestamps and fake process identities; no I/O.
public sealed class WorkerBuildTerminalDecisionTests
{
    private static readonly DateTimeOffset Requested = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 9, 0, TimeSpan.Zero);
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);
    private static readonly WorkerBuildProcess Worker = new(4242, Requested.AddMinutes(-1), @"C:\fake\worker.exe");
    private static readonly string[] Projects = ["src/Feature/One.csproj", "src/Feature/Two.csproj", "src/Feature/Three.csproj"];

    private static readonly WorkerBuildObservation[] Building =
    [
        new(WorkerBuildPhase.Requested, Requested),
        new(WorkerBuildPhase.WaitingForCustody, Requested),
        new(WorkerBuildPhase.CustodyAcquired, Requested.AddMinutes(1)),
        new(WorkerBuildPhase.WaitingForBuildSlot, Requested.AddMinutes(1)),
        new(WorkerBuildPhase.BuildSlotAcquired, Requested.AddMinutes(3)),
        new(WorkerBuildPhase.PreparingLaunch, Requested.AddMinutes(3)),
        new(WorkerBuildPhase.ChildRegistered, Requested.AddMinutes(3)),
        new(WorkerBuildPhase.CompilerStarted, Requested.AddMinutes(3)),
        new(WorkerBuildPhase.PreparingLaunch, Requested.AddMinutes(4)),
        new(WorkerBuildPhase.ChildRegistered, Requested.AddMinutes(4)),
        new(WorkerBuildPhase.CompilerStarted, Requested.AddMinutes(4))
    ];

    [Fact]
    public void CompleteZeroExits_CompileDeadline_ReturnsSuccess()
    {
        var result = Decide(Building, Projects[..2], [0, 0], StopCause.CompileDeadline, Budget, Now);
        Assert.Equal(WorkerBuildOutcome.Success, result.Outcome);
        Assert.Equal(0, result.CompilerExitCode);
        Assert.Equal("Host observed all scoped compiler/build exits successfully.", result.Diagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservedFailure_CompleteOrPartialScope_OutranksDeadline(bool complete)
    {
        var result = Decide(Building, complete ? Projects[..2] : Projects, [0, 1], StopCause.CompileDeadline, Budget, Now);
        Assert.Equal(WorkerBuildOutcome.CompilerFailure, result.Outcome);
        Assert.Equal(1, result.CompilerExitCode);
        Assert.Equal("Host observed compiler/build exit failure.", result.Diagnostic);
    }

    [Fact]
    public void PartialExits_CompileDeadline_NamesCurrentProjectAndDurations()
    {
        var result = Decide(Building, Projects, [0, null], StopCause.CompileDeadline, Budget, Now);
        Assert.Equal(WorkerBuildOutcome.TimeoutAfterStart, result.Outcome);
        Assert.Null(result.CompilerExitCode);
        Assert.Equal("Compile deadline 5m00s passed 6m00s after build slot acquisition while project 2/3 src/Feature/Two.csproj was building; 1 earlier projects exited 0; queue 3m00s (custody 1m00s, slot 2m00s).",
            result.Diagnostic);
    }

    [Fact]
    public void SlotAcquiredWithoutLaunch_CompileDeadline_ExplainsInterruption()
    {
        WorkerBuildObservation[] observations =
        [
            new(WorkerBuildPhase.Requested, Requested),
            new(WorkerBuildPhase.CustodyAcquired, Requested.AddMinutes(1)),
            new(WorkerBuildPhase.WaitingForBuildSlot, Requested.AddMinutes(1)),
            new(WorkerBuildPhase.BuildSlotAcquired, Requested.AddMinutes(3))
        ];
        var result = Decide(observations, Projects, [], StopCause.CompileDeadline, Budget, Now);
        Assert.Equal(WorkerBuildOutcome.Interrupted, result.Outcome);
        Assert.Null(result.CompilerExitCode);
        Assert.Equal("Compile deadline 5m00s passed before project 1/3 src/Feature/One.csproj launched; 0 earlier projects exited 0; queue 3m00s (custody 1m00s, slot 2m00s).",
            result.Diagnostic);
    }

    [Fact]
    public void SlotWait_QueueDeadline_ExplainsCapacity()
    {
        WorkerBuildObservation[] observations =
        [
            new(WorkerBuildPhase.Requested, Requested),
            new(WorkerBuildPhase.CustodyAcquired, Requested.AddMinutes(1)),
            new(WorkerBuildPhase.WaitingForBuildSlot, Requested.AddMinutes(1))
        ];
        var result = Decide(observations, Projects, [], StopCause.QueueDeadline, Budget, Now);
        Assert.Equal(WorkerBuildOutcome.CapacityBeforeStart, result.Outcome);
        Assert.Null(result.CompilerExitCode);
        Assert.Equal("Queue deadline 5m00s passed while waiting for a build slot; waited 8m00s after custody (1m00s).", result.Diagnostic);
    }

    [Fact]
    public void CustodyWait_QueueDeadline_ExplainsApparatusFailure()
    {
        WorkerBuildObservation[] observations =
        [new(WorkerBuildPhase.Requested, Requested), new(WorkerBuildPhase.WaitingForCustody, Requested)];
        var result = Decide(observations, Projects, [], StopCause.QueueDeadline, Budget, Now);
        Assert.Equal(WorkerBuildOutcome.ApparatusFailure, result.Outcome);
        Assert.Null(result.CompilerExitCode);
        Assert.Equal("Queue deadline 5m00s passed while waiting for goal evidence custody (9m00s).", result.Diagnostic);
    }

    [Theory]
    [InlineData(false, "Worker process 4242 exited during WaitingForCustody.")]
    [InlineData(true, "Worker process 4242 exited during ChildRegistered at project 1/3 src/Feature/One.csproj.")]
    public void WorkerExit_BeforeOrAfterRegistration_ExplainsCancellation(bool registered, string diagnostic)
    {
        WorkerBuildObservation[] observations = registered
            ? [new(WorkerBuildPhase.Requested, Requested), new(WorkerBuildPhase.ChildRegistered, Requested.AddMinutes(3))]
            : [new(WorkerBuildPhase.Requested, Requested), new(WorkerBuildPhase.WaitingForCustody, Requested)];
        var probed = false;
        var result = ObserveWorker(process =>
        {
            Assert.Equal(Worker, process);
            probed = true;
            return false;
        });
        Assert.True(probed);
        Assert.Equal(WorkerBuildOutcome.Cancelled, result.Outcome);
        Assert.Null(result.CompilerExitCode);
        Assert.Equal(diagnostic, result.Diagnostic);

        (WorkerBuildOutcome Outcome, int? CompilerExitCode, string Diagnostic) ObserveWorker(Func<WorkerBuildProcess, bool> isAlive) =>
            Decide(observations, Projects, registered ? [null] : [],
                isAlive(Worker) ? StopCause.None : StopCause.WorkerExited, Budget, Now, Worker.Pid);
    }

    [Theory]
    [InlineData("dispatch", "Dispatch cancelled during CompilerStarted at project 2/3 src/Feature/Two.csproj.")]
    [InlineData("lifetime", "Worker request lifetime ended during CompilerStarted at project 2/3 src/Feature/Two.csproj.")]
    public void ExplicitCancellation_ExplainsCauseAndProject(string cause, string diagnostic)
    {
        var result = Decide(Building, Projects, [0, null],
            cause == "dispatch" ? StopCause.DispatchCancelled : StopCause.LifetimeEnded, Budget, Now);
        Assert.Equal(WorkerBuildOutcome.Cancelled, result.Outcome);
        Assert.Null(result.CompilerExitCode);
        Assert.Equal(diagnostic, result.Diagnostic);
    }

    // Literal mapping at 1227c4666, WorkerBuildRequestHost:161-166. Deadline rows use the compile cause.
    [Theory]
    [InlineData(true, true, true, true, true, false, false, WorkerBuildOutcome.Cancelled)]
    [InlineData(false, false, true, true, true, false, false, WorkerBuildOutcome.Cancelled)]
    [InlineData(false, true, false, true, true, false, false, WorkerBuildOutcome.Cancelled)]
    [InlineData(false, true, true, true, true, false, false, WorkerBuildOutcome.TimeoutAfterStart)]
    [InlineData(false, true, true, false, true, false, false, WorkerBuildOutcome.Interrupted)]
    [InlineData(false, true, true, false, false, true, false, WorkerBuildOutcome.Interrupted)]
    [InlineData(false, true, true, false, false, false, true, WorkerBuildOutcome.CapacityBeforeStart)]
    [InlineData(false, true, true, false, false, false, false, WorkerBuildOutcome.ApparatusFailure)]
    public void BaselineOutcomeTable_PreservesAllEightMappings(bool lifetimeCancelled, bool deadlineFired,
        bool workerAlive, bool started, bool slotHeld, bool preparing, bool slotWait, WorkerBuildOutcome expected)
    {
        List<WorkerBuildObservation> observations =
        [new(WorkerBuildPhase.Requested, Requested), new(WorkerBuildPhase.WaitingForCustody, Requested)];
        if (slotWait || slotHeld || preparing || started)
        {
            observations.Add(new(WorkerBuildPhase.CustodyAcquired, Requested.AddMinutes(1)));
            observations.Add(new(WorkerBuildPhase.WaitingForBuildSlot, Requested.AddMinutes(1)));
        }
        if (slotHeld) observations.Add(new(WorkerBuildPhase.BuildSlotAcquired, Requested.AddMinutes(3)));
        if (preparing) observations.Add(new(WorkerBuildPhase.PreparingLaunch, Requested.AddMinutes(3)));
        if (started)
        {
            observations.Add(new(WorkerBuildPhase.ChildRegistered, Requested.AddMinutes(3)));
            observations.Add(new(WorkerBuildPhase.CompilerStarted, Requested.AddMinutes(3)));
        }
        var cause = lifetimeCancelled ? StopCause.LifetimeEnded : !workerAlive ? StopCause.WorkerExited
            : !deadlineFired ? StopCause.None : StopCause.CompileDeadline;
        var result = Decide(observations, Projects, started ? [null] : [], cause, Budget, Now, Worker.Pid);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.CompilerExitCode);
    }

    [Fact]
    public void QueueDeadline_WithAcquiredSlot_RejectsImpossibleState()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Decide(Building, Projects, [0, null], StopCause.QueueDeadline, Budget, Now));
    }
}
