using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist;

[Collection(TestCollections.LandingGitRunner)]
public sealed class AcceptanceCohortWorkflowTestsLandingDenylist : AcceptanceCohortWorkflowTests
{
    private const string Watched = "src/Mcg.AgentOrchestrator.App/Orchestration/RemoteGitMirror.cs";
    private const string Ordinary = "src/Ordinary.cs";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EachMemberRecordsOwnAdmissionOrRecoveredReceipt(bool train, bool recover)
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[] { CreateCompletedGoal(kernel, "Matched member", repo), CreateCompletedGoal(kernel, "Ordinary member", repo) };
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var candidates = new[]
            {
                CreateCandidate(repo, goals[0].Id.Value, Watched, "matched"),
                CreateCandidate(repo, goals[1].Id.Value, Ordinary, "ordinary")
            };
            foreach (var (goal, candidate) in goals.Zip(candidates))
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["Full gate passes"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate.Revision);
            }
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            string commit;
            if (train)
            {
                var bindings = new[]
                {
                    TrainBind(candidates[0].GoalId, candidates[0].Revision, Watched, "resource:first"),
                    TrainBind(candidates[1].GoalId, candidates[1].Revision, Ordinary, "resource:second")
                };
                using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings);
                commit = integration.CommitRevision;
                var identity = MergeTrainIdentity.Create(integration.Members, main, integration.TreeRevision, "manifest-v1");
                var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
                var receipt = store.SaveGateReceipt(new MergeTrainReceipt("denylist-train", identity,
                    MergeTrainGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                    GateTestResultPaths: [WritePassingTrx(repo, "denylist-train.trx")], ValidForLanding: true));
                if (recover)
                {
                    store.PrepareLanding(receipt, commit, main);
                    AdvanceForRecovery(repo, main, commit);
                }
                else
                {
                    var result = LandingExecutor.ExecuteMergeTrain(kernel, goals, workspace, receipt, commit,
                        store, ConductorAutonomyPolicy.Permissive, eventWriter: writer);
                    Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, result.Outcome);
                }
            }
            else
            {
                var bindings = new[]
                {
                    Bind(candidates[0].GoalId, candidates[0].Revision, Watched, "resource:first"),
                    Bind(candidates[1].GoalId, candidates[1].Revision, Ordinary, "resource:second")
                };
                using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
                commit = integration.CommitRevision;
                var identity = AcceptanceCohortIdentity.Create(bindings, main, integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(integration.Path, [Watched, Ordinary]));
                var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
                var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt("denylist-cohort", identity,
                    AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                    GateTestResultPaths: [WritePassingTrx(repo, "denylist-cohort.trx")], ValidForLanding: true));
                if (recover)
                {
                    store.PrepareLanding(receipt, commit, main);
                    AdvanceForRecovery(repo, main, commit);
                }
                else
                {
                    var result = LandingExecutor.ExecuteCohort(kernel, goals, workspace, receipt, commit,
                        store, ConductorAutonomyPolicy.Permissive, eventWriter: writer);
                    Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, result.Outcome);
                }
            }
            if (recover)
                _ = new ConductorDriver(kernel, workspace,
                    FakeAcceptanceVerifier.Throws(new InvalidOperationException("Recovery must not run a gate.")),
                    AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                    cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
            Assert.Equal(commit, RunGitOutput(repo, "rev-parse", "main").Trim());
            for (var index = 0; index < goals.Length; index++)
            {
                using var line = ReadLanded(workspace, goals[index].Id);
                Assert.Equal(candidates[index].Revision, line.RootElement.GetProperty("admissionCandidateSha").GetString());
                Assert.Equal(recover ? "recovered-landing" : index == 0 ? "denylist-match-recorded" : "green-gate-auto",
                    line.RootElement.GetProperty("admissionRule").GetString());
                Assert.Equal(recover ? "not-evaluated" : "built-in-default", line.RootElement.GetProperty("landingDenylistSource").GetString());
                if (!recover && index == 0) Assert.Equal([$"git-mutation={Watched}"], ReadMatches(line));
                else Assert.Empty(ReadMatches(line));
            }
        }
        finally { DeleteDirectory(repo); }
    }

    private static void AdvanceForRecovery(string repo, string main, string commit)
    {
        RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
        RunGit(repo, "update-ref", $"refs/heads/{LandingExecutor.IntegrationBranchName}", commit, main);
        RunGit(repo, "update-ref", "refs/heads/main", commit, main);
        RunGit(repo, "read-tree", "-m", "-u", main, commit);
    }
}
