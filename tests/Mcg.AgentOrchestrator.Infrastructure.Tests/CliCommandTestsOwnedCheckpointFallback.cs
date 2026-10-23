using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Isolated real stores; collection protects the CLI base's environment setup.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsOwnedCheckpointFallback : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task UnsupportedMerge_RepeatedCheckpoints_PersistsStateAndAdvancesBaseline(bool asyncFailure)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Target", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            var proxy = TransactionOnlyRepository.Wrap(repository, out var calls);
            calls.AsyncFailure = asyncFailure;
            var checkpoint = new CliOwnedGoalCheckpoint(proxy, kernel, goal.Id);

            kernel.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "First checkpoint");
            checkpoint.Persist(kernel);
            var request = kernel.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "Second checkpoint");
            checkpoint.Persist(kernel);
            checkpoint.Persist(kernel);

            var durable = await repository.LoadAsync();
            Assert.Contains(durable.GetGoal(goal.Id).Timeline, item => item.Message == "First checkpoint");
            Assert.Contains(durable.HumanInputRequests, item => item.Id == request.Id);
            Assert.Equal(2, calls.MergeAttempts);
            Assert.Equal(2, calls.Transactions);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task UnsupportedMerge_ConcurrentMutation_RejectsWithoutLosingStoredState(bool terminal)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Target", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var other = kernel.CreateGoal("Other", [new TaskSpec(TaskId.New(), "Independent", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(other.Id, AgentCatalog.Default().Agents);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            var proxy = TransactionOnlyRepository.Wrap(repository, out var calls);
            var checkpoint = new CliOwnedGoalCheckpoint(proxy, kernel, goal.Id);
            if (terminal)
                kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Completed });
            else
                kernel.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "Stale progress");

            // The hook runs at the fallback transaction boundary, after the old holder prepared
            // its checkpoint. No timer or scheduler ordering determines this interleaving.
            string? stored = null;
            calls.BeforeTransaction = () =>
            {
                repository.TransactAsync((fresh, _) =>
                {
                    fresh.AddTask(goal.Id, AgentRole.Tester, "Concurrent verification");
                    fresh.RequestHumanInput(other.Id, other.Tasks[0].Id, "Unrelated request");
                    return Task.FromResult((true, true));
                }).GetAwaiter().GetResult();
                stored = JsonSerializer.Serialize(repository.LoadAsync().GetAwaiter().GetResult().ExportSnapshot());
            };

            var failure = Assert.Throws<InvalidOperationException>(() => checkpoint.Persist(kernel));
            Assert.Contains("COMMAND_CHECKPOINT_REJECTED", failure.Message);
            Assert.True(failure.Data.Contains("SubmittedGoalState"));
            Assert.True(failure.Data.Contains("StoredGoalState"));
            Assert.NotNull(stored);
            Assert.Equal(stored, JsonSerializer.Serialize((await repository.LoadAsync()).ExportSnapshot()));
            Assert.Equal(1, calls.MergeAttempts);
            Assert.Equal(1, calls.Transactions);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Xunit.Fact]
    public async Task MergeFailure_SupportedOperationFails_DoesNotTryAnotherWrite()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Target", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            await repository.SaveAsync(kernel);
            var proxy = TransactionOnlyRepository.Wrap(repository, out var calls);
            var expected = new InvalidOperationException("Merge failed, not unsupported");
            calls.MergeFailure = expected;
            var checkpoint = new CliOwnedGoalCheckpoint(proxy, kernel, goal.Id);
            kernel.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "Must not fall back");

            Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => checkpoint.Persist(kernel)));
            Assert.Equal(1, calls.MergeAttempts);
            Assert.Equal(0, calls.Transactions);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    public class TransactionOnlyRepository : DispatchProxy
    {
        private ITransactionalOrchestratorStateRepository _inner = null!;
        public bool AsyncFailure { get; set; }
        public Exception MergeFailure { get; set; } = new NotSupportedException("Merge persistence is optional");
        public Action? BeforeTransaction { get; set; }
        public int MergeAttempts { get; private set; }
        public int Transactions { get; private set; }

        public static ITransactionalOrchestratorStateRepository Wrap(
            ITransactionalOrchestratorStateRepository inner, out TransactionOnlyRepository calls)
        {
            var proxy = Create<ITransactionalOrchestratorStateRepository, TransactionOnlyRepository>();
            calls = (TransactionOnlyRepository)proxy;
            calls._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (method.Name == nameof(ITransactionalOrchestratorStateRepository.SaveGoalSnapshotsWithMergeAsync))
            {
                MergeAttempts++;
                if (AsyncFailure)
                    return Task.FromException<IReadOnlyList<GoalSnapshotSaveResult>>(MergeFailure);
                throw MergeFailure;
            }
            if (method.Name == nameof(IOrchestratorStateRepository.SaveAsync))
                throw new InvalidOperationException("Unsafe whole-state save reached");
            if (method.Name == nameof(ITransactionalOrchestratorStateRepository.TransactGoalStateAsync))
            {
                Transactions++;
                var before = BeforeTransaction;
                BeforeTransaction = null;
                before?.Invoke();
            }
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
