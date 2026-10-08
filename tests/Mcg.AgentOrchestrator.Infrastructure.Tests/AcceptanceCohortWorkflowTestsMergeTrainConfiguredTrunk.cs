using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all refs, SQLite receipts and artifacts live in this test's own repository.
public sealed class AcceptanceCohortWorkflowTestsMergeTrainConfiguredTrunk : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void ExecuteMergeTrain_MasterOnlyRepository_AdvancesTestedCommit()
    {
        var repo = CreateSeededRepository();
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
            var workspace = OrchestratorWorkspace.ForProject("alpha", repo, integrationBranch: "master");
            var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            var identity = MergeTrainIdentity.Create(bindings, master, tree, "manifest-v1");
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt("master-train-receipt", identity,
                MergeTrainGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "master-train.trx")], ValidForLanding: true));

            var result = LandingExecutor.ExecuteMergeTrain(kernel, goals, workspace, receipt, commit,
                store, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, result.Outcome);
            Assert.Equal(commit, RunGitOutput(repo, "rev-parse", "refs/heads/master").Trim());
            Assert.NotEqual(0, GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/main").ExitCode);
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            for (var index = 0; index < goals.Length; index++)
                Assert.Equal($"member {index}", File.ReadAllText(Path.Combine(repo, "src", $"member-{index}.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
