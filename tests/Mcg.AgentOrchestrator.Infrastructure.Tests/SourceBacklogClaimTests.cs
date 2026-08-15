using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class SourceBacklogClaimTests
{
    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Cancelled, false, GoalReplacementDisposition.ZeroWorkCorrection, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Cancelled, true, GoalReplacementDisposition.ZeroWorkCorrection, GoalReplacementOutcome.IneligibleDisposition)]
    [Xunit.InlineData(GoalStatus.Cancelled, true, GoalReplacementDisposition.SupersedeUnlandedAttempt, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Failed, true, GoalReplacementDisposition.AbandonFailedAttempt, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Superseded, true, GoalReplacementDisposition.SupersedeUnlandedAttempt, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Cancelled, false, GoalReplacementDisposition.Unspecified, GoalReplacementOutcome.IneligibleDisposition)]
    [Xunit.InlineData(GoalStatus.Active, false, GoalReplacementDisposition.ZeroWorkCorrection, GoalReplacementOutcome.ProtectedOwner)]
    public void EligibilityUsesExplicitDispositionMatrix(
        GoalStatus status,
        bool hasWork,
        GoalReplacementDisposition disposition,
        GoalReplacementOutcome expected)
    {
        var facts = new GoalReplacementEligibilityFacts(
            status,
            HasWorkspace: hasWork,
            HasBranch: false,
            HasDispatch: false,
            HasRepositoryDelta: false,
            IsRunning: false,
            IsLanded: false,
            IsMerged: false,
            IsRecorded: false);

        Xunit.Assert.Equal(expected, SourceBacklogClaimEligibility.Evaluate(disposition, facts));
    }

    [Xunit.Fact]
    public async Task LegacyMultipleLinksFailClosedWithoutInventingOwner()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal("First historical source association");
        var second = kernel.CreateGoal("Second historical source association");
        const string backlogItemId = "legacy-ambiguous-source";
        kernel.SetGoalSourceBacklogItemLink(first.Id, backlogItemId, SourceBacklogCoverage.Full);
        kernel.SetGoalSourceBacklogItemLink(second.Id, backlogItemId, SourceBacklogCoverage.Full);
        await repository.SaveAsync(kernel);

        var exception = Xunit.Assert.Throws<LegacySourceBacklogOwnerAmbiguousException>(() =>
            new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(kernel, backlogItemId));

        Xunit.Assert.Equal(backlogItemId, exception.BacklogItemId);
        Xunit.Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(), exception.LinkedGoalIds.Order());
    }

    [Xunit.Fact]
    public void OrdinaryClaimInitializationRequiresAmbientStateTransaction()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Ordinary source-linked goal");
        const string backlogItemId = "ordinary-standalone-claim";
        kernel.SetGoalSourceBacklogItemLink(goal.Id, backlogItemId, SourceBacklogCoverage.Slice);
        var store = new SourceBacklogClaimStore(workspace.SqliteStatePath);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            store.EnsureClaimForNewGoal(
                kernel,
                backlogItemId,
                goal.Id.Value,
                SourceBacklogCoverage.Slice));

        Xunit.Assert.Contains("must join the active state transaction", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Null(store.ResolveClaim(new AgentOrchestratorKernel(), backlogItemId));
    }

    [Xunit.Fact]
    public void ProtectedLandingEvidenceOverridesOtherwiseEligibleTerminalStatus()
    {
        var facts = new GoalReplacementEligibilityFacts(
            GoalStatus.Cancelled,
            HasWorkspace: false,
            HasBranch: false,
            HasDispatch: false,
            HasRepositoryDelta: false,
            IsRunning: false,
            IsLanded: true,
            IsMerged: false,
            IsRecorded: false);

        Xunit.Assert.Equal(
            GoalReplacementOutcome.ProtectedOwner,
            SourceBacklogClaimEligibility.Evaluate(GoalReplacementDisposition.ZeroWorkCorrection, facts));
    }

    [Xunit.Fact]
    public void UnavailableGitEvidenceFailsClosed()
    {
        var facts = new GoalReplacementEligibilityFacts(
            GoalStatus.Cancelled,
            HasWorkspace: false,
            HasBranch: false,
            HasDispatch: false,
            HasRepositoryDelta: false,
            IsRunning: false,
            IsLanded: false,
            IsMerged: false,
            IsRecorded: false,
            IsGitEvidenceAvailable: false);

        Xunit.Assert.Equal(
            GoalReplacementOutcome.ProtectedOwner,
            SourceBacklogClaimEligibility.Evaluate(GoalReplacementDisposition.ZeroWorkCorrection, facts));
    }
}
