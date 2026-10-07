using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the scenario owns its repository, metadata, store and build slots.
public sealed class AcceptanceCohortWorkflowTestsFollowerGateStop : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void ExactLeaderLandingDoesNotStopOrSupersedeFollower()
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var stops = 0;
        scenario.Enable(driver, stop: _ => { stops++; return true; });
        var record = scenario.Start(driver);
        scenario.LandLeader();
        driver.StopInvalidatedFollowerGates();
        driver.StopInvalidatedFollowerGates();
        Assert.Equal(0, stops);
        var current = Assert.Single(scenario.Records());
        Assert.Equal("Running", current.Outcome);
        Assert.Null(current.ReconciledAt);
        Assert.Null(scenario.Store.TryReadReceipt(record.IdentityValue));
        Assert.Equal(new[] { scenario.Follower.Id.Value }, driver.GetActiveCohortGateMemberGoalIds());
        Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetSupersedableGateAttempts([scenario.Follower.Id.Value]));
    }

    [Theory]
    [InlineData("failed", FollowerGateInvalidReason.LeaderFailed, "leader-failed")]
    [InlineData("stale", FollowerGateInvalidReason.LeaderFailed, "leader-failed")]
    [InlineData("different-tree", FollowerGateInvalidReason.LeaderTreeDiffers, "leader-tree-differs")]
    [InlineData("base-moved", FollowerGateInvalidReason.BaseMoved, "base-moved")]
    public void InvalidLeaderStopsOnceAndPersistsReason(string movement, FollowerGateInvalidReason reason, string wire)
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var stops = 0;
        var events = new List<string>();
        scenario.Enable(driver, stop: _ => { stops++; return true; }, events: events.Add);
        var record = scenario.Start(driver);
        switch (movement)
        {
            case "failed":
                scenario.Kernel.BeginGoalAcceptanceVerification(scenario.Leader.Id, "Run leader gate");
                scenario.Kernel.ReconcileGoalAcceptanceFailed(scenario.Leader.Id, ["leader-check"], "Leader gate red");
                Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Leader.Status);
                Assert.Equal(record.MainRevision, RunGitOutput(scenario.Repo, "rev-parse", "main"));
                break;
            case "stale":
                CreateWorktreeCandidate(scenario.Repo, scenario.Leader.Id, "new-leader.txt", "stale leader candidate");
                break;
            case "different-tree":
                CreateWorktreeCandidate(scenario.Repo, scenario.Leader.Id, "extra.txt", "different landing tree");
                scenario.LandLeader();
                Assert.Equal(record.MainRevision, RunGitOutput(scenario.Repo, "rev-parse", "main^1"));
                break;
            case "base-moved":
                scenario.CommitOnMain("first-unrelated.txt", "first unrelated change");
                scenario.CommitOnMain("second-unrelated.txt", "second unrelated change");
                Assert.NotEqual(record.MainRevision, RunGitOutput(scenario.Repo, "rev-parse", "main^1"));
                break;
            default: throw new InvalidOperationException($"Unknown movement {movement}.");
        }
        driver.StopInvalidatedFollowerGates();
        driver.StopInvalidatedFollowerGates();
        Assert.Equal(1, stops);
        var reconciled = Assert.Single(scenario.Records());
        Assert.Equal("Reconciled", reconciled.Outcome);
        Assert.NotNull(reconciled.ReconciledAt);
        Assert.Equal($"follower-invalidated:{wire}", reconciled.Detail);
        var receipt = Assert.IsType<FollowerGateRunReceipt>(scenario.Store.TryReadReceipt(record.IdentityValue));
        Assert.Equal(FollowerGateRunOutcome.Invalidated, receipt.Outcome);
        Assert.Equal(reason, receipt.InvalidReason);
        Assert.Equal(record.IdentityValue, FollowerGateIdentity.Create(receipt.Binding));
        Assert.Single(events, line => line == $"FOLLOWER_GATE_INVALIDATED leader={scenario.Leader.Id.Value} follower={scenario.Follower.Id.Value} reason={wire}");
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
    }

    [Fact]
    public void FailedStopKeepsRunningAttemptAndRetriesOnNextSweep()
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var stops = 0;
        scenario.Enable(driver, stop: _ => ++stops > 1);
        var record = scenario.Start(driver);
        scenario.Kernel.BeginGoalAcceptanceVerification(scenario.Leader.Id, "Run leader gate");
        scenario.Kernel.ReconcileGoalAcceptanceFailed(scenario.Leader.Id, ["leader-check"], "Leader gate red");
        driver.StopInvalidatedFollowerGates();
        Assert.Equal(1, stops);
        Assert.Null(Assert.Single(scenario.Records()).ReconciledAt);
        Assert.Null(scenario.Store.TryReadReceipt(record.IdentityValue));
        // The active-member query exercises the grouped sweep stop call.
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        Assert.Equal(2, stops);
        Assert.Equal(FollowerGateRunOutcome.Invalidated, scenario.Store.TryReadReceipt(record.IdentityValue)?.Outcome);
    }

    [Fact]
    public void ExistingChildReceiptWinsOverStopReceipt()
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([FailedVerification(scenario.Repo, "red.trx", "red")]));
        scenario.Enable(driver);
        var record = scenario.Start(driver);
        driver.RunGroupedGateAttemptBody(record);
        scenario.Kernel.BeginGoalAcceptanceVerification(scenario.Leader.Id, "Run leader gate");
        scenario.Kernel.ReconcileGoalAcceptanceFailed(scenario.Leader.Id, ["leader-check"], "Leader gate red");
        driver.StopInvalidatedFollowerGates();
        Assert.Equal(FollowerGateRunOutcome.Failed, scenario.Store.TryReadReceipt(record.IdentityValue)?.Outcome);
        Assert.Null(scenario.Store.TryReadReceipt(record.IdentityValue)?.InvalidReason);
        Assert.Equal("follower-invalidated:leader-failed", Assert.Single(scenario.Records()).Detail);
    }
}
