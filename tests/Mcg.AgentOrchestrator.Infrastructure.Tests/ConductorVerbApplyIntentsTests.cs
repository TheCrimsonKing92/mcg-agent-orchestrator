using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns a GUID workspace, databases and lease path;
// no shared environment, process mutation or timing-based synchronization is used.
public sealed class ConductorVerbApplyIntentsTests
{
    [Fact]
    public async Task ActiveOwner_RefusesBeforeRepositoryOpenAndPreservesPendingIntent()
    {
        using var fixture = new Fixture();
        var seed = await CreateSeed(fixture);
        var baseline = await seed.Repository.LoadGoalAsync(seed.GoalId);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var repositoryOpened = false;

        var exit = CliConductorCommand.Run(["conductor", "apply-intents", seed.GoalId.Value[..8]],
            fixture.Workspace, lockProbe: new ConductorVerbStartTests.FixedProbe(4242),
            output: output, error: error, stateRepository: _ =>
            {
                repositoryOpened = true;
                throw new InvalidOperationException("Repository must not be opened.");
            });

        Assert.Equal(1, exit);
        Assert.StartsWith("Refused: conductor running (pid 4242", error.ToString());
        Assert.Contains("next tick", error.ToString());
        Assert.Empty(output.ToString());
        Assert.False(repositoryOpened);
        Assert.Equal(OperatorIntentStatus.Pending, (await seed.Store.GetAsync(seed.Intent.Id))!.Status);
        await AssertSnapshotUnchanged(seed, baseline);
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public void ActiveOwner_RefusesWithoutInitializingAnyStore()
    {
        using var fixture = new Fixture();
        using var error = new StringWriter();

        Assert.Equal(1, CliConductorCommand.Run(["conductor", "apply-intents", "12345678"],
            fixture.Workspace, lockProbe: new ConductorVerbStartTests.FixedProbe(4242), error: error));

        Assert.StartsWith("Refused: conductor running (pid 4242", error.ToString());
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "operator-intents.db")));
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public async Task HeldLease_RefusesDespiteEmptyProbeAndPreservesPendingIntent()
    {
        using var fixture = new Fixture();
        var seed = await CreateSeed(fixture);
        var baseline = await seed.Repository.LoadGoalAsync(seed.GoalId);
        using var lease = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory);
        Assert.True(File.Exists(fixture.LockPath));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = Run(fixture, seed.GoalId.Value[..8], output, error);

        Assert.Equal(1, exit);
        Assert.Contains("conduct-loop.lock", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal(OperatorIntentStatus.Pending, (await seed.Store.GetAsync(seed.Intent.Id))!.Status);
        await AssertSnapshotUnchanged(seed, baseline);
        Assert.True(File.Exists(fixture.LockPath));
    }

    [Fact]
    public async Task PendingProgress_AppliesOnceAndSecondRunReportsNothingPending()
    {
        using var fixture = new Fixture();
        var seed = await CreateSeed(fixture);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, Run(fixture, seed.GoalId.Value[..8].ToUpperInvariant(), output, error));
        Assert.Contains(seed.Intent.Id, output.ToString());
        Assert.Empty(error.ToString());
        await AssertAppliedOnce(seed);
        Assert.False(File.Exists(fixture.LockPath));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, Run(fixture, seed.GoalId.Value, output, error));
        Assert.Equal($"No pending operator intents for goal {seed.GoalId.Value[..8]}{Environment.NewLine}",
            output.ToString());
        Assert.Empty(error.ToString());
        await AssertAppliedOnce(seed);
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public async Task CompetingSave_ReportsNotAppliedAndFreshRunRecoversClaimOnce()
    {
        using var fixture = new Fixture();
        var seed = await CreateSeed(fixture);
        var baseline = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
        var competingObjective = baseline.Objective + " (competing)";
        var repository = new CompetingSaveRepository(seed.Repository, seed.GoalId, competingObjective);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, Run(fixture, seed.GoalId.Value[..8], output, error, _ => repository));
        Assert.True(repository.CompetingSavePerformed);
        Assert.StartsWith("Not applied: ", error.ToString());
        Assert.Contains("next applier run or conductor tick", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal(OperatorIntentStatus.Claimed, (await seed.Store.GetAsync(seed.Intent.Id))!.Status);
        var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
        Assert.Equal(competingObjective, stored.Objective);
        Assert.DoesNotContain(stored.Timeline, item => item.OperatorIntentApplied?.IntentId == seed.Intent.Id);
        Assert.False(File.Exists(fixture.LockPath));

        error.GetStringBuilder().Clear();
        Assert.Equal(0, Run(fixture, seed.GoalId.Value[..8], output, error));
        Assert.Contains(seed.Intent.Id, output.ToString());
        Assert.Empty(error.ToString());
        await AssertAppliedOnce(seed);
        Assert.Equal(competingObjective, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Objective);
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public async Task UnknownPrefix_ReportsNotFoundAndPreservesPendingIntent()
    {
        using var fixture = new Fixture();
        var seed = await CreateSeed(fixture);
        var baseline = await seed.Repository.LoadGoalAsync(seed.GoalId);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, Run(fixture, "unknown", output, error));
        Assert.Equal($"Error: Goal 'unknown' was not found.{Environment.NewLine}", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal(OperatorIntentStatus.Pending, (await seed.Store.GetAsync(seed.Intent.Id))!.Status);
        await AssertSnapshotUnchanged(seed, baseline);
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public async Task SharedPrefix_ReportsAmbiguousAndExactGoalLeavesOtherIntentPending()
    {
        using var fixture = new Fixture();
        var seed = await CreateSeed(fixture, new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"));
        var otherId = new GoalId("abc10000bbbbbbbbbbbbbbbbbbbbbbbb");
        var otherTask = new TaskSpec(TaskId.New(), "Other pending progress", AgentRole.Developer);
        await seed.Repository.TransactAsync((kernel, _) =>
        {
            kernel.CreateGoal(otherId, "Other goal", [otherTask]);
            kernel.ActivateGoal(otherId, AgentCatalog.Default().Agents);
            return Task.FromResult((true, true));
        });
        var otherIntent = CreateIntent(otherId, otherTask.Id);
        await seed.Store.EnqueueAsync(otherIntent);
        var baseline = await seed.Repository.LoadGoalAsync(seed.GoalId);
        var otherBaseline = await seed.Repository.LoadGoalAsync(otherId);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, Run(fixture, "ABC10000", output, error));
        Assert.Equal($"Error: Goal prefix 'ABC10000' is ambiguous.{Environment.NewLine}", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Equal(OperatorIntentStatus.Pending, (await seed.Store.GetAsync(seed.Intent.Id))!.Status);
        await AssertSnapshotUnchanged(seed, baseline);
        Assert.False(File.Exists(fixture.LockPath));

        error.GetStringBuilder().Clear();
        Assert.Equal(0, Run(fixture, seed.GoalId.Value, output, error));
        await AssertAppliedOnce(seed);
        Assert.Empty(error.ToString());
        Assert.Equal(OperatorIntentStatus.Pending, (await seed.Store.GetAsync(otherIntent.Id))!.Status);
        Assert.Equal(JsonSerializer.Serialize(otherBaseline),
            JsonSerializer.Serialize(await seed.Repository.LoadGoalAsync(otherId)));
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongPrefixCount_ReportsUsageWithoutOpeningState(bool twoPrefixes)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = twoPrefixes
            ? ["conductor", "apply-intents", "first", "second"]
            : ["conductor", "apply-intents"];

        Assert.Equal(1, CliConductorCommand.Run(args, fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), output: output, error: error));
        Assert.Equal(CliCommandHelp.ConductorUsage + Environment.NewLine, error.ToString());
        Assert.Empty(output.ToString());
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(fixture.LockPath));
    }

    private static int Run(Fixture fixture, string prefix, TextWriter output, TextWriter error,
        Func<OrchestratorWorkspace, ITransactionalOrchestratorStateRepository>? repository = null) =>
        CliConductorCommand.Run(["conductor", "apply-intents", prefix], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), output: output, error: error,
            stateRepository: repository);

    private static async Task AssertSnapshotUnchanged(Seed seed, GoalSnapshot? baseline) =>
        Assert.Equal(JsonSerializer.Serialize(baseline),
            JsonSerializer.Serialize(await seed.Repository.LoadGoalAsync(seed.GoalId)));

    private static async Task AssertAppliedOnce(Seed seed)
    {
        Assert.Equal(OperatorIntentStatus.Applied, (await seed.Store.GetAsync(seed.Intent.Id))!.Status);
        var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
        var marker = Assert.Single(stored.Timeline, item => item.OperatorIntentApplied?.IntentId == seed.Intent.Id);
        Assert.Equal("applied", marker.OperatorIntentApplied!.Outcome);
        Assert.Equal(WorkTaskStatus.Running, Assert.Single(stored.Tasks).Status);
    }

    private static async Task<Seed> CreateSeed(Fixture fixture, GoalId? id = null)
    {
        StateDbMigrations.EnsureUpToDate(fixture.Workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Apply pending progress", AgentRole.Developer);
        var goal = kernel.CreateGoal(id ?? GoalId.New(), "Offline operator progress", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(kernel);
        var store = SqliteOperatorIntentStore.ForDirectories(
            fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        var intent = CreateIntent(goal.Id, task.Id);
        await store.EnqueueAsync(intent);
        return new Seed(repository, goal.Id, store, intent);
    }

    private static OperatorIntentRecord CreateIntent(GoalId goalId, TaskId taskId) => new(
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Progress,
        goalId.Value, taskId.Value,
        JsonSerializer.Serialize(new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "Offline progress."),
            JsonSerializerOptions.Web),
        [], "operator", "cli", "local-process", DateTimeOffset.UtcNow);

    private sealed record Seed(SqliteOrchestratorStateRepository Repository, GoalId GoalId,
        SqliteOperatorIntentStore Store, OperatorIntentRecord Intent);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-cli-offline-intents-{Guid.NewGuid():N}");
        internal OrchestratorWorkspace Workspace { get; }
        internal string LockPath => Path.Combine(Workspace.OrchestratorDirectory, "conduct-loop.lock");
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class CompetingSaveRepository(
        ITransactionalOrchestratorStateRepository inner,
        GoalId goalId,
        string objective) : ITransactionalOrchestratorStateRepository
    {
        internal bool CompetingSavePerformed { get; private set; }

        public async Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
            IReadOnlyCollection<GoalSnapshotSaveRequest> goals, CancellationToken cancellationToken = default)
        {
            var request = Assert.Single(goals);
            Assert.Equal(goalId.Value, request.Baseline.Id);
            Assert.True(request.RejectConflict);
            await inner.TransactGoalStateAsync(goalId, (state, _) =>
            {
                Assert.NotNull(state);
                var brief = Assert.Single(state.Goal.BriefVersions!);
                var competing = state.Goal with
                {
                    Objective = objective,
                    BriefVersions = [brief with { Text = objective }]
                };
                return Task.FromResult((true, (GoalStateSnapshot?)(state with { Goal = competing }), true));
            }, cancellationToken);
            CompetingSavePerformed = true;
            var results = await inner.SaveGoalSnapshotsWithMergeAsync(goals, cancellationToken);
            Assert.Equal(GoalSnapshotSaveDisposition.Skipped, Assert.Single(results).Disposition);
            return results;
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(goalId, Assert.Single(goalIds));
            return inner.LoadGoalsAsync(goalIds, cancellationToken);
        }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);
        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(kernel, cancellationToken);
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);
        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListConductLoopGoalMetadataAsync(cancellationToken);
        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => inner.ListGoalIdsWithCompletedHumanInputAsync(goalIds, cancellationToken);
        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            inner.ListModelFitHistoryAsync(cancellationToken);
        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize, CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);
        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) =>
            inner.QueryBestFitForRoleAsync(role, cancellationToken);
        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactAsync(transaction, cancellationToken);
        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactAsync(transaction, cancellationToken);
        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default) =>
            inner.LoadGoalAsync(goalId, cancellationToken);
        public Task SaveGoalSnapshotsAsync(IReadOnlyCollection<GoalSnapshot> goals, CancellationToken cancellationToken = default) =>
            inner.SaveGoalSnapshotsAsync(goals, cancellationToken);
        public Task<T> TransactGoalAsync<T>(GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactGoalAsync(goalId, transaction, cancellationToken);
        public Task<T> TransactGoalStateAsync<T>(GoalId goalId,
            Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactGoalStateAsync(goalId, transaction, cancellationToken);
    }
}
