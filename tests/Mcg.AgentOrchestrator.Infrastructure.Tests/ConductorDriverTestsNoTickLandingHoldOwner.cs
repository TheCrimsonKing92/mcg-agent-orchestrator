using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Process launch/liveness and build slots are seamed; the deadline advances only via the injected clock.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsNoTickLandingHoldOwner
{
    [Fact]
    public void NoTickLandingDeadline_HeldCarriesBackgroundAttemptOwner()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SimpleGoal("No-tick landing hold owner");
            PassVerification(kernel, goal, goal.Tasks.Single());
            var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
            var launches = 0;
            var polls = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                executionDirectory: root,
                utcNow: () => now,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9801);
                },
                acquireStableSlotLease: (_, _) => null);
            var driver = new ConductorDriver(
                _ => GoalLifecycleFacts.None,
                () => 0,
                _ => root,
                _ => DispatchStartOutcome.Started(),
                null,
                null,
                _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                null,
                null,
                null,
                (_, _, _) => 0,
                null,
                _ => new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "Already fast-forwardable", [], null),
                (candidate, _) => new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed"),
                null,
                _ => { },
                _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
                (_, _, _) => { },
                _ => ChangeRiskTier.DocsOnly,
                runAcceptanceVerificationWithLease: (_, _, _, _) =>
                    throw new InvalidOperationException("The held owned process must not run in this test."),
                parallelAcceptanceAttemptCoordinator: coordinator,
                noTickAcceptancePollDelay: delay =>
                {
                    polls++;
                    now += delay;
                },
                noTickAcceptancePollTimeout: TimeSpan.FromMilliseconds(200),
                utcNow: () => now,
                executionDirectory: root);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Contains("bounded no-tick wait", held.Reason, StringComparison.Ordinal);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Equal(ConductorHoldOwner.BackgroundAttempt, held.Owner);
            Assert.Equal(1, launches);
            Assert.True(polls > 0, "The injected poll delay must advance the deadline.");
            var attemptPath = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, goal.Id.Value), "*.attempt.json"));
            var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(attemptPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(attempt);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, attempt.Outcome);
            Assert.Null(attempt.ReconciledAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
