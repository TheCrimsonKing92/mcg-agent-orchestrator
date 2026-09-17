using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class ConductorBatchLoopTestsParallelAcceptance
{
    [Xunit.Fact]
    public void AcceptanceWidthOneStartsExactlyOneAttemptAndHoldsTheOtherAtPolicyCap()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        using var liveGateProbe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 2)
            .Select(index => CreateVerifiedSimpleGoal(
                kernel,
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/WidthOne{index}.cs"))
            .ToArray();
        var attemptRoot = CreateTempDirectory("mcg-acceptance-width-one");
        Action waitForAttempts = () => { };
        var acceptanceCalls = 0;

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    Interlocked.Increment(ref acceptanceCalls);
                    started.Set();
                    release.Wait(TestContext.Current.CancellationToken);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                    [$"src/Mcg.AgentOrchestrator.App/Orchestration/{goal.Id.Value[..8]}.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator);
            BatchTickSummary? tick = null;

            var summary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
                NoStopPath(),
                maxIterations: 1,
                onTick: current => tick = current);

            started.Wait(TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref acceptanceCalls));
            Assert.Equal(2, summary.Held);
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal) &&
                line.Contains("cap=1", StringComparison.Ordinal));
            Assert.Single(coordinator.GetCapacityReservingAttempts(goals.Select(goal => goal.Id.Value)));
        }
        finally
        {
            release.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }
}
