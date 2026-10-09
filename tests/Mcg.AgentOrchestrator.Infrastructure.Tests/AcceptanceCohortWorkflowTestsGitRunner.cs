using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.LandingGitRunner)]
public sealed class AcceptanceCohortWorkflowTestsGitRunner : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void IntegrationCreationFailure_TombstonesPreparedGoalLandingIntents()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First creation-failure member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second creation-failure member", repo);
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
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-integration-create-failure", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                100, [], 0, [WritePassingTrx(repo, "receipt-integration-create-failure.trx")], ValidForLanding: true));
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args.Length == 3 &&
                args[0] == "update-ref" &&
                args[1] == $"refs/heads/{LandingExecutor.IntegrationBranchName}"
                    ? new GitCli.GitResult(1, string.Empty, "fixture integration creation failure")
                    : GitCli.Run(workingDirectory, args);

            var result = LandingExecutor.ExecuteCohort(
                kernel, [firstGoal, secondGoal], workspace, receipt, integration.CommitRevision, store,
                ConductorAutonomyPolicy.Conservative);

            Assert.Equal(AcceptanceCohortLandingOutcome.RetryableHold, result.Outcome);
            Assert.False(result.MainAdvanced);
            Assert.Equal(main, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.False(GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/integration").Succeeded);
            Assert.All(
                [firstGoal, secondGoal],
                goal => Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id))));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void MainCasFailure_RestoresIntegrationRef_AndLandsNeitherMember()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First CAS member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second CAS member", repo);
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
            var integrationBefore = RunGitOutput(
                repo,
                "rev-parse",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim();
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-cas", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                100, [], 0, [WritePassingTrx(repo, "receipt-cas.trx")], ValidForLanding: true));
            var moved = false;
            LandingExecutor.GitRunner = (workingDirectory, args) =>
            {
                var isOldMerge = args.SequenceEqual(["merge", "--ff-only", integration.CommitRevision]);
                var isMainCas = args.Length == 4 && args[0] == "update-ref" && args[1] == "refs/heads/main";
                if (!moved && (isOldMerge || isMainCas))
                {
                    moved = true;
                    var movement = GitCli.Run(workingDirectory, "reset", "--hard", first.Revision);
                    Assert.Equal(0, movement.ExitCode);
                }
                return GitCli.Run(workingDirectory, args);
            };

            var result = LandingExecutor.ExecuteCohort(
                kernel, [firstGoal, secondGoal], workspace, receipt, integration.CommitRevision, store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(moved);
            Assert.Equal(AcceptanceCohortLandingOutcome.StateInvalidated, result.Outcome);
            Assert.False(result.MainAdvanced);
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(
                integrationBefore,
                RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE landed=1;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            DeleteDirectory(repo);
        }
    }

}
