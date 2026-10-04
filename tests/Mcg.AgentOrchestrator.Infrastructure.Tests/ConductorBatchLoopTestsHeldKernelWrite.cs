using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsHeldKernelWrite : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsHeldKernelWrite(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void HeldFindingEvidenceReceiptAndRetryAreWrittenInTheSameTick()
    {
        var fixture = new ConductorFindingEvidenceLoopFixture();
        var persistedIds = new HashSet<GoalId>();
        var result = new ConductorBatchLoop(utcNow: () => ConductorFindingEvidenceLoopFixture.Now).Run(
            fixture.Kernel, fixture.CreateDriver(), ConductorAutonomyPolicy.Permissive, NoStopPath(),
            maxIterations: 1, persistGoalTick: (_, ids) => persistedIds.UnionWith(ids));

        Assert.Equal(1, fixture.FocusedRuns);
        Assert.Equal(1, result.Held);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, Assert.Single(fixture.DispatchStates));
        Assert.Equal(fixture.ReviewerId, Assert.Single(fixture.Retries).TaskId);
        var reviewer = fixture.Kernel.Goals.Single().Tasks.Single(task => task.Id == fixture.ReviewerId);
        Assert.Single(reviewer.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []));
        Assert.Contains(fixture.Goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded);
        Assert.Contains(fixture.Goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        Assert.Contains(fixture.Goal.Id, persistedIds);
    }

    [Xunit.Fact]
    public void HeldAdvanceWithoutKernelMutationStaysUnwritten()
    {
        var (kernel, goal) = SimpleGoal();
        var persistedIds = new HashSet<GoalId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => ConductorFindingEvidenceLoopFixture.HeldDispatch());
        var result = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Permissive, NoStopPath(), maxIterations: 1,
            persistGoalTick: (_, ids) => persistedIds.UnionWith(ids));

        Assert.Equal(1, result.Held);
        Assert.DoesNotContain(goal.Id, persistedIds);
    }
}
