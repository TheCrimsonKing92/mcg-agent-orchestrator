using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fact owns and cleans its repository and receipt store.
public sealed class AcceptanceCohortWorkflowTestsFollowerGateCarry : AcceptanceCohortWorkflowTests
{
    private const string Carry = "conductor:acceptance-follower-carry";
    private const string Discard = "conductor:acceptance-follower-discard";
    private static readonly ConductorAutonomyPolicy Enabled = ConductorAutonomyPolicy.Permissive with { FollowerGatesEnabled = true };

    [Fact]
    public void ExactLandingCarriesOnceAndLandsTestedTree()
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var receipt = SaveReceipt(scenario, driver, FollowerGateRunOutcome.Passed);
        scenario.LandLeader();
        var leaderMain = RunGitOutput(scenario.Repo, "rev-parse", "main");
        Assert.Null(driver.CarryFollowerGateReceipt(scenario.Follower, ConductorAutonomyPolicy.Permissive));
        var result = Assert.IsType<FollowerGateCarryResult>(driver.CarryFollowerGateReceipt(scenario.Follower, Enabled));
        Assert.Equal("LandFollower", result.Reason);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Result!.Outcome);
        Assert.Equal(receipt.Binding.FollowerTestedTree, RunGitOutput(scenario.Repo, "rev-parse", "main^{tree}"));
        var entry = Assert.Single(GoalOperationJournal.Read(scenario.Repo, scenario.Follower.Id).Entries.Where(entry => entry.Operation == Carry));
        Assert.Equal("passed", entry.AcceptanceOutcome);
        Assert.Equal(leaderMain, entry.MainHeadSha);
        Assert.NotEqual(receipt.Binding.FollowerBranchHead, entry.BranchHeadSha);
        Assert.Equal(receipt.Binding.FollowerTestedTree, RunGitOutput(scenario.Repo, "rev-parse", $"{entry.BranchHeadSha}^{{tree}}"));
        Assert.Contains($"receipt={receipt.ReceiptId}", entry.Detail);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(scenario.Follower, Enabled).Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(scenario.Follower, Enabled).Outcome);
        Assert.Equal(GoalStatus.Completed, scenario.Follower.Status);
        Assert.Null(driver.CarryFollowerGateReceipt(scenario.Follower, Enabled));
        Assert.Single(GoalOperationJournal.Read(scenario.Repo, scenario.Follower.Id).Entries.Where(entry => entry.Operation == Carry));
    }

    [Fact]
    public void ExactLandingChargesFailedChecksWithoutCarryPass()
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var receipt = SaveReceipt(scenario, driver, FollowerGateRunOutcome.Failed);
        scenario.LandLeader();
        var leaderMain = RunGitOutput(scenario.Repo, "rev-parse", "main");
        var result = Assert.IsType<FollowerGateCarryResult>(driver.CarryFollowerGateReceipt(scenario.Follower, Enabled));
        Assert.Equal("ChargeFollower", result.Reason);
        Assert.Equal(GoalStatus.AcceptanceFailed, scenario.Follower.Status);
        Assert.Equal(leaderMain, RunGitOutput(scenario.Repo, "rev-parse", "main"));
        Assert.Equal(receipt.FailedChecks, scenario.Follower.LatestAcceptanceFailure!.FailedChecks);
        Assert.Equal(leaderMain, scenario.Follower.LatestAcceptanceFailure.MainHeadSha);
        Assert.Equal(RunGitOutput(GoalWorktrees.TryResolve(scenario.Repo, scenario.Follower.Id)!, "rev-parse", "HEAD"),
            scenario.Follower.LatestAcceptanceFailure.BranchHeadSha);
        Assert.Empty(GoalOperationJournal.Read(scenario.Repo, scenario.Follower.Id).Entries.Where(entry => entry.Operation == Carry));
    }

    [Fact]
    public void MovedBaseDiscardsReceiptOnceWithoutLanding()
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var receipt = SaveReceipt(scenario, driver, FollowerGateRunOutcome.Passed);
        scenario.LandLeader();
        scenario.CommitOnMain("unrelated.txt", "unrelated");
        var main = RunGitOutput(scenario.Repo, "rev-parse", "main");
        var result = Assert.IsType<FollowerGateCarryResult>(driver.CarryFollowerGateReceipt(scenario.Follower, Enabled));
        Assert.Equal("base-moved", result.Reason);
        Assert.Null(result.Result);
        Assert.Equal(GoalStatus.Verified, scenario.Follower.Status);
        Assert.Equal(main, RunGitOutput(scenario.Repo, "rev-parse", "main"));
        Assert.Null(driver.CarryFollowerGateReceipt(scenario.Follower, Enabled));
        var entries = GoalOperationJournal.Read(scenario.Repo, scenario.Follower.Id).Entries;
        var discard = Assert.Single(entries.Where(entry => entry.Operation == Discard));
        Assert.Contains($"receipt={receipt.ReceiptId}", discard.Detail);
        Assert.Contains("reason=base-moved", discard.Detail);
        Assert.Empty(entries.Where(entry => entry.Operation == Carry));
    }

    [Fact]
    public void DisabledCarryDoesNotReadReceiptRebaseOrLand()
    {
        using var scenario = AcceptanceCohortWorkflowTestsFollowerGateStart.CreateScenario();
        var driver = scenario.Driver(new SequenceAcceptanceVerifier([]));
        var receipt = SaveReceipt(scenario, driver, FollowerGateRunOutcome.Passed);
        scenario.LandLeader();
        var main = RunGitOutput(scenario.Repo, "rev-parse", "main");
        Assert.Null(driver.CarryFollowerGateReceipt(scenario.Follower, ConductorAutonomyPolicy.Permissive));
        Assert.Equal(receipt.Binding.FollowerBranchHead,
            RunGitOutput(GoalWorktrees.TryResolve(scenario.Repo, scenario.Follower.Id)!, "rev-parse", "HEAD"));
        Assert.Equal(main, RunGitOutput(scenario.Repo, "rev-parse", "main"));
        Assert.Equal(GoalStatus.Verified, scenario.Follower.Status);
        Assert.Empty(GoalOperationJournal.Read(scenario.Repo, scenario.Follower.Id).Entries.Where(entry => entry.Operation is Carry or Discard));
    }

    private static FollowerGateRunReceipt SaveReceipt(AcceptanceCohortWorkflowTestsFollowerGateStart.Scenario scenario,
        ConductorDriver driver, FollowerGateRunOutcome outcome)
    {
        var members = ProjectSelection(driver, scenario.Leader, scenario.Follower).Members;
        using var workspace = Assert.IsType<FollowerGateWorkspace>(GoalWorktrees.CreateFollowerWorkspace(scenario.Repo,
            members[0].MainRevision, members[0].GoalId, members[0].CandidateRevision,
            members[1].GoalId, members[1].BranchRevision, scenario.Cleanup).Workspace);
        var binding = workspace.ToReceipt(GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(workspace.Path, members[1].LandingPaths));
        var receipt = new FollowerGateRunReceipt("carry-receipt", FollowerGateIdentity.Create(binding), binding, outcome,
            DateTimeOffset.UnixEpoch, outcome == FollowerGateRunOutcome.Failed ? ["follower-check"] : [],
            outcome == FollowerGateRunOutcome.Failed ? 1 : 0, [WritePassingTrx(scenario.Repo, "follower.trx")], null);
        scenario.Store.SaveGateReceipt(receipt);
        return receipt;
    }
}
