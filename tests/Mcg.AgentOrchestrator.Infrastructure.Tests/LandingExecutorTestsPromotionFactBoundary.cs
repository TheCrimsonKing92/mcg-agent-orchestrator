using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Serialized with the Landing lane because GitRunner is an existing shared test seam.
// Every Git repository/database is private; correctness uses the injected instant.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsPromotionFactBoundary
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AncestorBranch_NoNewPromotion_RetainsUnknownDateWithoutSamplingClock()
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/ancestor.txt");
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            var branch = GoalWorktrees.BranchName(goal.Id);
            Assert.True(GitCli.Run(root, "merge", "--ff-only", branch).Succeeded);
            var main = GitCli.Run(root, "rev-parse", "main").Output.Trim();
            var candidate = GitCli.Run(root, "rev-parse", branch).Output.Trim();
            Assert.Equal(main, candidate);
            GoalOperationJournal.AcceptancePassed(root, goal, "conductor:acceptance", candidate, main,
                "Fresh acceptance of an already-integrated ancestor branch");

            var result = LandingExecutor.Execute(kernel, goal, workspace, promotionClock: new UnusedClock());

            Assert.IsType<LandingDecision.Promote>(result.Decision);
            Assert.Equal(main, GitCli.Run(root, "rev-parse", "main").Output.Trim());
            Assert.Null(Assert.Single(await repository.ListGoalMetadataAsync()).PromotionFact);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task NativePromotion_LaterObservationAndRestart_KeepOriginalWindow()
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/promotion.txt");
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            var prior = GitCli.Run(root, "rev-parse", "main").Output.Trim();
            var candidate = GitCli.Run(root, "rev-parse", GoalWorktrees.BranchName(goal.Id)).Output.Trim();
            Assert.NotEqual(prior, candidate); // the native promotion boundary must actually be selected
            var result = LandingExecutor.Execute(kernel, goal, workspace, promotionClock: new FixedClock(At));
            Assert.True(result.MainAdvanced, result.Message);
            var original = Assert.Single(await repository.ListGoalMetadataAsync()).PromotionFact;
            Assert.NotNull(original);
            Assert.Equal(goal.Id.Value, original.GoalId);
            Assert.Equal(prior, original.PriorMainRevision);
            Assert.Equal(candidate, original.CandidateRevision);
            Assert.Equal(result.MergeCommitSha, original.PromotedMainRevision);
            Assert.Equal(GitCli.Run(root, "rev-parse", "main").Output.Trim(), original.PromotedMainRevision);
            Assert.Equal(At, original.PromotedAt);
            Assert.Equal(GoalPromotionFact.SoloProvenance, original.Provenance);

            GoalOperationJournal.Completed(root, goal, "conductor:land", result.Message);
            kernel.CompleteGoal(goal.Id, "Publication note after original promotion");
            await repository.SaveAsync(kernel);
            var replay = LandingExecutor.Execute(kernel, goal, workspace, promotionClock: new UnusedClock());
            Assert.False(replay.MainAdvanced);
            Assert.Equal(original, Assert.Single(await SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                .ListGoalMetadataAsync()).PromotionFact);

            var epic = new PortfolioEpic("epic", "Promotion", null, At, "test", At, "test");
            PortfolioEpicMember member = new(epic.Id, PortfolioMemberKind.Goal, goal.Id.Value, At, "test");
            var metadata = await repository.ListGoalMetadataAsync();
            Assert.Equal(1, Assert.Single(EpicProgressReadModel.Build([epic], [], [member], metadata, [], At)).WindowLandedCount);
            Assert.Equal(0, Assert.Single(EpicProgressReadModel.Build([epic], [], [member], metadata, [], At.AddTicks(1))).WindowLandedCount);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostPromotionFailure_Recovery_RetainsKnownOrUnknownDate(bool failPersistence)
    {
        var root = LandingExecutorTests.CreateGitRepository();
        var previousRunner = LandingExecutor.GitRunner;
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/failure.txt");
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            var mainBefore = GitCli.Run(root, "rev-parse", "main").Output.Trim();
            if (failPersistence)
                ExecuteSql(workspace.SqliteStatePath, "CREATE TRIGGER fail_fact BEFORE INSERT ON goal_promotion_facts BEGIN SELECT RAISE(ABORT, 'forced fact failure'); END");
            else
                LandingExecutor.GitRunner = (directory, args) => args[0] == "read-tree"
                    ? new GitCli.GitResult(1, "", "forced checkout failure") : previousRunner(directory, args);

            Assert.ThrowsAny<Exception>(() => LandingExecutor.Execute(kernel, goal, workspace,
                promotionClock: new FixedClock(At)));
            var mainAfter = GitCli.Run(root, "rev-parse", "main").Output.Trim();
            Assert.NotEqual(mainBefore, mainAfter);
            var fact = Assert.Single(await repository.ListGoalMetadataAsync()).PromotionFact;
            if (failPersistence)
            {
                Assert.Null(fact);
                Assert.NotNull(GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(root, goal.Id)));
                ExecuteSql(workspace.SqliteStatePath, "DROP TRIGGER fail_fact");
            }
            else
            {
                Assert.NotNull(fact);
                Assert.Equal(At, fact.PromotedAt);
                Assert.Equal(mainAfter, fact.PromotedMainRevision);
            }
            LandingExecutor.GitRunner = previousRunner;
            var recovered = LandingExecutor.Execute(kernel, goal, workspace, promotionClock: new UnusedClock());
            Assert.IsType<LandingDecision.Promote>(recovered.Decision);
            Assert.Equal(fact, Assert.Single(await repository.ListGoalMetadataAsync()).PromotionFact);
            LandingExecutor.Execute(kernel, goal, workspace, promotionClock: new UnusedClock());
            Assert.Equal(fact, Assert.Single(await repository.ListGoalMetadataAsync()).PromotionFact);
        }
        finally
        {
            LandingExecutor.GitRunner = previousRunner;
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOrFailedMainMutation_ProducesNoPromotionFact(bool pending)
    {
        var root = LandingExecutorTests.CreateGitRepository();
        var previousRunner = LandingExecutor.GitRunner;
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/negative.txt");
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            var mainBefore = GitCli.Run(root, "rev-parse", "main").Output.Trim();
            var reachedMainMutation = false;
            if (!pending)
                LandingExecutor.GitRunner = (directory, args) =>
                {
                    if (args[0] == "update-ref" && args.Contains("refs/heads/main"))
                    {
                        reachedMainMutation = true;
                        return new GitCli.GitResult(1, "", "forced main compare-and-swap failure");
                    }
                    return previousRunner(directory, args);
                };
            var result = LandingExecutor.Execute(kernel, goal, workspace,
                mutationBlocker: pending ? () => "pending operator gate" : null, promotionClock: new UnusedClock());
            Assert.False(result.MainAdvanced);
            Assert.Equal(!pending, reachedMainMutation);
            Assert.Equal(mainBefore, GitCli.Run(root, "rev-parse", "main").Output.Trim());
            Assert.Null(Assert.Single(await repository.ListGoalMetadataAsync()).PromotionFact);
        }
        finally
        {
            LandingExecutor.GitRunner = previousRunner;
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private sealed class FixedClock(DateTimeOffset at) : IClock { public DateTimeOffset UtcNow => at; }
    private sealed class UnusedClock : IClock
    {
        public DateTimeOffset UtcNow => throw new InvalidOperationException("Recovery or failed promotion sampled a new time.");
    }
    private static void ExecuteSql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
