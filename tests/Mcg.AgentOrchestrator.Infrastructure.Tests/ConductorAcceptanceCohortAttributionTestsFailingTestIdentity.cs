using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortAttributionTestsFailingTestIdentity : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void UnrelatedPartitionFailure_DoesNotAttributeThatMember()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "Cohort T member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Unrelated U member", repo);
            CreateWorktreeCandidate(repo, firstGoal.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new SequenceAcceptanceVerifier([
                FailedVerification(repo, "cohort-T.trx", "cohort") with
                { Checks = [new AcceptanceCheckResult("cohort", false, 1, "T", FailingTestIdentities: ["Tests.T"])] },
                FailedVerification(repo, "partition-T.trx", "first") with
                { Checks = [new AcceptanceCheckResult("first", false, 1, "T", FailingTestIdentities: ["Tests.T"])] },
                FailedVerification(repo, "partition-U.trx", "second") with
                { Checks = [new AcceptanceCheckResult("second", false, 1, "U", FailingTestIdentities: ["Tests.U"])] }
            ]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, verifier.RunCount);
            Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, result.Receipt?.Attribution);
            var attributed = Assert.Single(result.Receipt!.AttributedMembers);
            Assert.Equal(firstGoal.Id, attributed.GoalId);
            Assert.Equal(selection.Members[0].CandidateRevision, attributed.CandidateRevision);
            Assert.Equal(["Tests.T"], attributed.ReproducedFailingTests);
            var unrelated = Assert.Single(result.Receipt.UnrelatedFailures);
            Assert.Equal(secondGoal.Id, unrelated.GoalId);
            Assert.Equal(["Tests.U"], unrelated.FailingTests);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var persisted = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(result.Receipt.Identity.Value));
            Assert.Equal(["Tests.T"], Assert.Single(persisted.AttributedMembers).ReproducedFailingTests);
            Assert.Equal(["Tests.U"], Assert.Single(persisted.UnrelatedFailures).FailingTests);
            Assert.Equal(1, store.ReadOvertakeCount(secondGoal.Id));
            Assert.Equal(0, store.ReadOvertakeCount(firstGoal.Id));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void BothReproduce_AndInfrastructurePrecedence()
    {
        var first = Partition("11111111111111111111111111111111", AcceptanceCohortGateOutcome.Failed, "Tests.T");
        var second = Partition("22222222222222222222222222222222", AcceptanceCohortGateOutcome.Failed, "Tests.T");
        var both = ConductorAcceptanceCohortFailingTestAttribution.Classify(["Tests.T"], first, second);
        Assert.Equal(AcceptanceCohortAttributionOutcome.BothMembersFailed, both.Outcome);
        Assert.Equal(2, both.AttributedMembers.Count);
        var unrelated = ConductorAcceptanceCohortFailingTestAttribution.Classify(
            ["Tests.T"], first with { FailingTestIdentities = ["Tests.U"] },
            second with { FailingTestIdentities = ["Tests.V"] });
        Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, unrelated.Outcome);
        Assert.Empty(unrelated.AttributedMembers);
        Assert.Equal(2, unrelated.UnrelatedFailures.Count);
        foreach (var invalid in new[] { AcceptanceCohortGateOutcome.InfrastructureFailure, AcceptanceCohortGateOutcome.Invalidated })
        {
            var result = ConductorAcceptanceCohortFailingTestAttribution.Classify(["Tests.T"], first, second with { Outcome = invalid });
            Assert.Equal(AcceptanceCohortAttributionOutcome.Indeterminate, result.Outcome);
            Assert.Empty(result.AttributedMembers);
        }
    }

    [Fact]
    public void FailedPartitionWithoutFailingTestIdentities_DoesNotAttributeThatMember()
    {
        var first = Partition("11111111111111111111111111111111", AcceptanceCohortGateOutcome.Failed, "Tests.T");
        var second = Partition("22222222222222222222222222222222", AcceptanceCohortGateOutcome.Failed, "Tests.U")
            with { FailingTestIdentities = [] };

        var result = ConductorAcceptanceCohortFailingTestAttribution.Classify(["Tests.T"], first, second);

        Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, result.Outcome);
        Assert.Equal(first.GoalId, Assert.Single(result.AttributedMembers).GoalId);
        Assert.Empty(result.UnrelatedFailures);
    }

    private static AcceptanceCohortPartitionReceipt Partition(string goal, AcceptanceCohortGateOutcome outcome, string test) =>
        new("partition", new GoalId(goal), goal[0] == '1' ? 0 : 1, "revision", "main", "tree", "manifest", outcome, 1, [])
        { FailingTestIdentities = [test] };
}
