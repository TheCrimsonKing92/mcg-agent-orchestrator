using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its repository, worktrees and receipts; the verifier is controlled.
public sealed class AcceptanceCohortBriefIdentityTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedChild_BriefBindingControlsExecution(bool obsoleteBrief)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[] { CreateCompletedGoal(kernel, "First brief member", repo),
                CreateCompletedGoal(kernel, "Second brief member", repo) };
            for (var index = 0; index < goals.Length; index++)
            {
                var goal = goals[index];
                var candidate = CreateWorktreeCandidate(repo, goal.Id,
                    index == 0 ? "src/Mcg.AgentOrchestrator.Infrastructure/BriefFirst.cs" : "tests/BriefSecond.cs",
                    $"member-{index}");
                kernel.RecordGoalRefinement(goal.Id, Spec(goal));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, goal.AuthoritativeBrief.Version,
                    CriterionEvidenceOwner.Acceptance, "test", CriterionEvidenceScopes.FullAcceptanceGate,
                    expectedCandidateSha: candidate);
            }
            var verifier = new SequenceAcceptanceVerifier([new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("combined", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "brief-child.trx")])]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true, cleanupHooks: cleanup.Hooks);
            var selection = ProjectSelection(driver, goals[0], goals[1]);
            var bindings = selection.BindMembers(id => kernel.GetGoal(id).AuthoritativeBrief.Version);
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo,
                       selection.Members[0].MainRevision, bindings, TrunkBranchName.Default, cleanup.Hooks))
            {
                var manifest = ((IGoalAcceptanceVerifier)verifier).ComputeEffectivePlanIdentity(integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray());
                identity = AcceptanceCohortIdentity.Create(bindings, selection.Members[0].MainRevision,
                    integration.TreeRevision, manifest);
            }
            Assert.All(identity.Members, member => Assert.Equal(
                kernel.GetGoal(member.GoalId).AuthoritativeBrief.Version, member.BriefVersion));
            var root = Path.Combine(workspace.OrchestratorDirectory, "brief-child");
            var attempt = new ConductorGroupedGateAttempt("brief-child", "cohort",
                selection.Members.Select(ConductorGroupedGateMember.From).ToArray(),
                identity.ObservedMainRevision, identity.CombinedTreeRevision, identity.ManifestIdentity,
                identity.Value, DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(root, "attempt.json"), Path.Combine(root, "result.json"),
                Path.Combine(root, "exit"), Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                repo, ConductorAutonomyPolicy.Permissive.ToJson());
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.Null(store.TryReadReceipt(identity.Value));
            if (obsoleteBrief)
            {
                kernel.ReviseGoalBrief(goals[0].Id, "Revised authoritative brief for the first member");
                Assert.NotEqual(identity.Members.Single(member => member.GoalId == goals[0].Id).BriefVersion,
                    goals[0].AuthoritativeBrief.Version);
                var error = Assert.Throws<InvalidOperationException>(() => driver.RunGroupedGateAttemptBody(attempt));
                Assert.Equal("Grouped cohort gate identity changed before child execution.", error.Message);
                Assert.Equal(0, verifier.RunCount);
                Assert.Null(store.TryReadReceipt(identity.Value));
            }
            else
            {
                var outcome = driver.RunGroupedGateAttemptBody(attempt);
                Assert.NotNull(outcome);
                Assert.Equal(1, verifier.RunCount);
                var receipt = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(identity.Value));
                Assert.Equal(AcceptanceCohortGateOutcome.Passed, receipt.Outcome);
                Assert.Equal(identity.Value, receipt.Identity.Value);
            }
        }
        finally { DeleteDirectory(repo); }
    }

    private static RefinedSpec Spec(Goal goal) => new(goal.Objective,
        ["The acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
    { AcceptanceGateOwnedAcceptanceCriteria = ["The acceptance gate passes"] };
}
