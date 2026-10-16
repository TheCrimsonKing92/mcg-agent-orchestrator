using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all refs, SQLite receipts and artifacts live in this test's own repository.
public sealed class AcceptanceCohortWorkflowTestsMergeTrainConfiguredTrunk : AcceptanceCohortWorkflowTests
{
    private sealed class PromotionClock : IClock
    {
        internal static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => At;
    }
    [Fact]
    public void ExecuteMergeTrain_MasterOnlyRepository_AdvancesTestedCommit()
    {
        var repo = CreateSeededRepository();
        var dataRoot = SharedTestSupport.CreateTempDirectory();
        try
        {
            RunGit(repo, "branch", "-M", "master");
            Assert.NotEqual(0, GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/main").ExitCode);
            var master = RunGitOutput(repo, "rev-parse", "master").Trim();
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3)
                .Select(index => CreateCompletedGoal(kernel, $"Train member {index}", repo)).ToArray();
            var bindings = goals.Select((goal, index) =>
            {
                var path = $"src/member-{index}.txt";
                RunGit(repo, "checkout", "-b", GoalWorktrees.BranchName(goal.Id), "master");
                Directory.CreateDirectory(Path.Combine(repo, "src"));
                File.WriteAllText(Path.Combine(repo, path), $"member {index}");
                RunGit(repo, "add", path);
                RunGit(repo, "commit", "-m", $"Member {index}");
                var revision = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
                RunGit(repo, "checkout", "master");
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["Train gate passes"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
                return TrainBind(goal.Id, revision, path, $"resource:{index}");
            }).ToArray();

            // Build the exact train tree without the out-of-slice worktree train helper.
            RunGit(repo, "checkout", "--detach", "master");
            for (var index = 0; index < bindings.Length; index++)
            {
                var priorHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
                RunGit(repo, "rebase", "--merge", "--no-stat", "--onto", priorHead, master,
                    bindings[index].CandidateRevision);
                bindings[index] = bindings[index].WithRebasedHead(
                    RunGitOutput(repo, "rev-parse", "HEAD").Trim());
            }
            var commit = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            var tree = RunGitOutput(repo, "rev-parse", "HEAD^{tree}").Trim();
            RunGit(repo, "checkout", "master");
            Assert.Equal(master, RunGitOutput(repo, "rev-parse", "master").Trim());
            Assert.NotEqual(master, commit);
            var workspace = OrchestratorWorkspace.ForProject("alpha", repo, integrationBranch: "master", dataRootDirectory: dataRoot);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            var identity = MergeTrainIdentity.Create(bindings, master, tree, "manifest-v1");
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt("master-train-receipt", identity,
                MergeTrainGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "master-train.trx")], ValidForLanding: true));

            var result = LandingExecutor.ExecuteMergeTrain(kernel, goals, workspace, receipt, commit,
                store, ConductorAutonomyPolicy.Permissive, promotionClock: new PromotionClock());

            Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, result.Outcome);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            repository.SaveAsync(kernel).GetAwaiter().GetResult();
            var facts = repository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            Assert.Equal(3, facts.Count);
            Assert.All(facts, row =>
            {
                Assert.NotNull(row.PromotionFact);
                Assert.Equal(PromotionClock.At, row.PromotionFact.PromotedAt);
                Assert.Equal(master, row.PromotionFact.PriorMainRevision);
                Assert.Equal(commit, row.PromotionFact.PromotedMainRevision);
                Assert.Equal(bindings.Single(member => member.GoalId.Value == row.Id).CandidateRevision,
                    row.PromotionFact.CandidateRevision);
                Assert.Equal(GoalPromotionFact.MergeTrainProvenance, row.PromotionFact.Provenance);
            });
            Assert.Equal(commit, RunGitOutput(repo, "rev-parse", "refs/heads/master").Trim());
            Assert.NotEqual(0, GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/main").ExitCode);
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            for (var index = 0; index < goals.Length; index++)
                Assert.Equal($"member {index}", File.ReadAllText(Path.Combine(repo, "src", $"member-{index}.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
            SharedTestSupport.RemoveTempDirectory(dataRoot);
        }
    }
}
