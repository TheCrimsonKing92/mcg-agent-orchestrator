using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsGoalPriorityAdmission : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsGoalPriorityAdmission(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(true, 1)]
    [Xunit.InlineData(false, 1)]
    [Xunit.InlineData(true, 4)]
    public void PriorityOrdersTheNextWorkerWithoutWithholdingCapacity(bool critical, int cap)
    {
        using var scope = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 4).Select(i => GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), $"Goal {i}")).ToArray();
        if (critical) kernel.SetGoalPriority(goals[3].Id, GoalPriority.Critical, "test", "setup");
        var dispatched = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => dispatched.Count,
            dispatchAndStart: goal =>
            {
                var task = goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned);
                kernel.RecordTaskDispatch(goal.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                dispatched.Add(goal.Id);
                return DispatchStartOutcome.Started();
            });
        var summary = new ConductorBatchLoop().Run(kernel, driver,
            ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = cap }, NoStopPath(), maxIterations: 1);

        Assert.Equal(cap, dispatched.Count);
        Assert.Equal(critical ? goals[3].Id : goals[0].Id, dispatched[0]);
        Assert.Equal(4 - cap, summary.Held);
        foreach (var goal in goals)
        {
            if (dispatched.Contains(goal.Id))
                Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains("At worker cap"));
            else
                Assert.Contains(goal.Timeline, evt => evt.Message.Contains("At worker cap (1/1); will advance when a slot opens"));
        }
    }

    [Xunit.Fact]
    public void CriticalDoesNotChangeAcceptanceAdmissionOrder()
    {
        using var scope = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        Assert.Equal(AcceptanceOrder(false), AcceptanceOrder(true));
    }

    private static IReadOnlyList<int> AcceptanceOrder(bool critical)
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 4).Select(i => GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), $"Update src/Mcg.AgentOrchestrator.App/Orchestration/PriorityScope{i}.cs")).ToArray();
        var verifiedAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        for (var i = 0; i < goals.Length; i++)
            PassVerificationAt(kernel, goals[i], goals[i].Tasks.Single(), verifiedAt.AddMinutes(i));
        if (critical) kernel.SetGoalPriority(goals[3].Id, GoalPriority.Critical, "test", "setup");
        var attemptRoot = CreateTempDirectory("mcg-priority-acceptance");
        var order = new List<int>();
        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    order.Add(Array.IndexOf(goals, goal));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal => [$"src/Mcg.AgentOrchestrator.App/Orchestration/PriorityScope{Array.IndexOf(goals, goal)}.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot, TrunkBranchName.Default, runInline: true));
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 4 },
                NoStopPath(), maxIterations: 1);
            Assert.Equal(new[] { 0, 1, 2, 3 }, order);
            return order;
        }
        finally { TryDeleteDirectory(attemptRoot); }
    }
}
