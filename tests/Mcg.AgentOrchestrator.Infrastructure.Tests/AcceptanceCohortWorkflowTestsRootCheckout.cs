using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.LandingGitRunner)]
public sealed class AcceptanceCohortWorkflowTestsRootCheckout : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void TrackedChangeHoldsCohortBeforeAnyRefMoves()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First root checkout member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second root checkout member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                repo, main, bindings, TrunkBranchName.Default);
            var identity = AcceptanceCohortIdentity.Create(
                bindings, main, integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path, bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-root-checkout-hold", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                100, [], 0, [WritePassingTrx(repo, "receipt-root-checkout-hold.trx")], ValidForLanding: true));
            var modified = Path.Combine(repo, "config", "acceptance-manifest.json");
            File.AppendAllText(modified, Environment.NewLine);
            var before = File.ReadAllBytes(modified);

            var result = LandingExecutor.ExecuteCohort(
                kernel, [firstGoal, secondGoal], workspace, receipt, integration.CommitRevision, store,
                ConductorAutonomyPolicy.Conservative);

            Assert.Equal(AcceptanceCohortLandingOutcome.RetryableHold, result.Outcome);
            Assert.False(result.MainAdvanced);
            Assert.Contains("config/acceptance-manifest.json", result.Message);
            Assert.Equal(main, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(main, RunGitOutput(repo, "rev-parse", LandingExecutor.IntegrationBranchName).Trim());
            Assert.All(new[] { firstGoal, secondGoal }, goal =>
                Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id))));
            Assert.Equal(before, File.ReadAllBytes(modified));
            foreach (var goal in new[] { firstGoal, secondGoal })
            {
                var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
                Assert.Contains("config/acceptance-manifest.json",
                    Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.LandingEscalation)).Message);
            }
        }
        finally { DeleteDirectory(repo); }
    }
}
