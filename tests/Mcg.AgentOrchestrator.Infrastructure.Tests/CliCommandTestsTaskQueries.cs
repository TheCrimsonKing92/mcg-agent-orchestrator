using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsTaskQueries : CliCommandTestBase
{
    [Xunit.Fact]
    public void TaskQueryCollaboratorAcceptsReadCapabilityWithoutMutationAuthority()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            IOrchestratorStateQueries queries = new QueryOnlyStateRepository(seed.Kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = seed.Target;
            var handled = false;

            var output = CaptureConsole(() => handled = CliTaskQueryCommand.TryExecute(
                ["task", "1"],
                queries,
                OrchestratorWorkspace.ForDirectory(root),
                new InMemoryModelProviderRegistry([]),
                channel: null,
                ref agents,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.True(handled);
            Xunit.Assert.Contains(seed.First.Description, output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void TaskUsesReadOnlyTargetedRepositoryPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var repository = new InMemoryTransactionalStateRepository(seed.Kernel);
            Goal? currentGoal = seed.Other;

            var output = ExecutePersistent(
                ["task", "--goal", seed.Target.Id.Value[..8], "1"],
                repository,
                workspace,
                ref currentGoal);

            Xunit.Assert.Contains(seed.First.Description, output, StringComparison.Ordinal);
            Xunit.Assert.Equal(seed.Target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(0, repository.TransactAsyncCount);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([seed.Target.Id.Value], repository.LoadedGoalIds);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(0, repository.SaveAsyncCount);
            Xunit.Assert.Equal(0, repository.SaveGoalSnapshotsCount);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void TaskSucceedsWhenMutationAndOutboxCapabilitiesThrow()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var repository = new InMemoryTransactionalStateRepository(seed.Kernel)
            {
                ThrowOnLoadAsync = true,
                ThrowOnWrite = true,
                ThrowOnTransact = true,
                ThrowOnOutbox = true
            };
            var outboxMessage = new OrchestratorStateOutboxMessage(
                "seeded-task-query-outbox",
                GoalOperationJournal.AcceptanceRetryAuditOutboxKind,
                "{}",
                DateTimeOffset.UtcNow);
            repository.SeedOutboxMessage(outboxMessage);
            Goal? currentGoal = seed.Other;

            var output = ExecutePersistent(
                ["task", "--goal", seed.Target.Id.Value[..8], "1"],
                repository,
                workspace,
                ref currentGoal);

            Xunit.Assert.Contains(seed.First.Description, output, StringComparison.Ordinal);
            Xunit.Assert.Equal(seed.Target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(0, repository.TransactAsyncCount);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([seed.Target.Id.Value], repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.True(repository.HasOutboxMessage(outboxMessage.Id));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void TaskAndTasksPreserveDispatcherOutputAndTargetSelection()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var cases = new (string[] Args, GoalId CurrentGoalId)[]
            {
                (["task", "1"], seed.Target.Id),
                (["task", seed.First.Id.Value[..8]], seed.Target.Id),
                (["task", "--goal", seed.Target.Id.Value[..8], "1"], seed.Other.Id),
                (["task", seed.Target.Id.Value[..8], "1"], seed.Other.Id),
                (["tasks"], seed.Target.Id),
                (["tasks", "status", WorkTaskStatus.Pending.ToString()], seed.Target.Id),
                (["tasks", "role", AgentRole.Developer.ToString()], seed.Target.Id),
                (["tasks", "id", seed.First.Id.Value[..8]], seed.Target.Id),
                (["tasks", "evidence", TaskEvidenceKind.None.ToString()], seed.Target.Id),
                (["tasks", "event", ProgressKind.GoalCreated.ToString()], seed.Target.Id)
            };

            foreach (var testCase in cases)
            {
                var expected = ExecuteLegacy(
                    testCase.Args,
                    seed.Kernel,
                    workspace,
                    testCase.CurrentGoalId,
                    out var expectedCurrentGoal);
                var repository = new InMemoryTransactionalStateRepository(seed.Kernel)
                {
                    ThrowOnLoadAsync = true,
                    ThrowOnWrite = true,
                    ThrowOnTransact = true,
                    ThrowOnOutbox = true
                };
                Goal? actualCurrentGoal = seed.Kernel.GetGoal(testCase.CurrentGoalId);

                var actual = ExecutePersistent(
                    testCase.Args,
                    repository,
                    workspace,
                    ref actualCurrentGoal);

                Xunit.Assert.Equal(expected, actual);
                Xunit.Assert.Equal(expectedCurrentGoal!.Id, actualCurrentGoal!.Id);
                Xunit.Assert.Equal(0, repository.LoadCount);
                Xunit.Assert.Equal(1, repository.LoadGoalsCount);
                Xunit.Assert.Equal([expectedCurrentGoal.Id.Value], repository.LoadedGoalIds);
                Xunit.Assert.Equal(0, repository.TransactAsyncCount);
                Xunit.Assert.Equal(0, repository.SaveAsyncCount);
                Xunit.Assert.Equal(0, repository.SaveGoalSnapshotsCount);
                Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void TaskQueryErrorsPreserveExceptionContracts()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var cases = new[]
            {
                new[] { "task", "99" },
                ["task", "deadbeef"],
                ["task", "feedface"],
                ["tasks", "status"],
                ["tasks", "unknown", "value"],
                ["task", "--bogus", "1"]
            };

            foreach (var args in cases)
            {
                var expected = CaptureLegacyException(args, seed.Kernel, workspace, seed.Target.Id);
                var repository = new InMemoryTransactionalStateRepository(seed.Kernel);
                Goal? currentGoal = seed.Target;

                var actual = Xunit.Record.Exception(() => ExecutePersistent(
                    args,
                    repository,
                    workspace,
                    ref currentGoal));

                Xunit.Assert.NotNull(expected);
                Xunit.Assert.NotNull(actual);
                Xunit.Assert.Equal(expected.GetType(), actual.GetType());
                Xunit.Assert.Equal(expected.Message, actual.Message);
                Xunit.Assert.Equal(0, repository.TransactAsyncCount);
                Xunit.Assert.Equal(1, repository.LoadGoalsCount);
                Xunit.Assert.Equal(0, repository.SaveAsyncCount);
                Xunit.Assert.Equal(0, repository.SaveGoalSnapshotsCount);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MissingAndAmbiguousGoalPrefixesPreserveLegacyErrors()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var cases = new[]
            {
                new[] { "task", "--goal", "missing0", "1" },
                ["task", "--goal", "abc", "1"],
                ["task", "--goal", seed.Target.Id.Value[..8]]
            };

            foreach (var args in cases)
            {
                var expected = CaptureLegacyException(args, seed.Kernel, workspace, seed.Target.Id);
                var repository = new InMemoryTransactionalStateRepository(seed.Kernel);
                Goal? currentGoal = seed.Target;

                var actual = Xunit.Record.Exception(() => ExecutePersistent(
                    args,
                    repository,
                    workspace,
                    ref currentGoal));

                Xunit.Assert.NotNull(expected);
                Xunit.Assert.NotNull(actual);
                Xunit.Assert.Equal(expected.GetType(), actual.GetType());
                Xunit.Assert.Equal(expected.Message, actual.Message);
                Xunit.Assert.Equal(args.Length < 4 ? 0 : 1, repository.ListGoalMetadataCount);
                Xunit.Assert.True(repository.TransactAsyncCount >= 1);
                Xunit.Assert.Equal(0, repository.SaveAsyncCount);
                Xunit.Assert.Equal(0, repository.SaveGoalSnapshotsCount);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void NullCurrentGoalPreservesLegacyLatestGoalFallback()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var expected = ExecuteLegacy(["tasks"], seed.Kernel, workspace, null, out var expectedCurrentGoal);
            var repository = new InMemoryTransactionalStateRepository(seed.Kernel);
            Goal? actualCurrentGoal = null;

            var actual = ExecutePersistent(["tasks"], repository, workspace, ref actualCurrentGoal);

            Xunit.Assert.Equal(expected, actual);
            Xunit.Assert.Equal(expectedCurrentGoal!.Id, actualCurrentGoal!.Id);
            Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            Xunit.Assert.True(repository.TransactAsyncCount >= 1);
            Xunit.Assert.Equal(0, repository.SaveAsyncCount);
            Xunit.Assert.Equal(0, repository.SaveGoalSnapshotsCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MutationCommandsDoNotEnterTaskQueryRoute()
    {
        var cases = new[]
        {
            new[] { "add-task", "Developer", "Added task" },
            ["progress", "1", "running", "Worker started"],
            ["retry", "1", "Retry with corrected input"],
            ["verify-manual", "1", "passed", "Operator verified"]
        };

        foreach (var args in cases)
        {
            var root = CreateTempDirectory();
            try
            {
                var workspace = CreateRefinedWorkspace(root);
                var seed = CreateSeed();
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                seed.Kernel.ActivateGoal(seed.Target.Id, agents);
                var repository = new InMemoryTransactionalStateRepository(seed.Kernel);
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = seed.Target;

                _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                    args,
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([]),
                    ref profiles,
                    ref currentGoal));

                Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
                Xunit.Assert.Equal(0, repository.LoadGoalsCount);
                Xunit.Assert.True(repository.ListOutboxMessagesCount >= 1);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SeedData CreateSeed()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(
            new TaskId("11111111aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Inspect target task",
            AgentRole.Developer);
        var second = new TaskSpec(
            new TaskId("22222222bbbbbbbbbbbbbbbbbbbbbbbb"),
            "Plan target task",
            AgentRole.Planner);
        var ambiguousA = new TaskSpec(
            new TaskId("feedfaceaaaaaaaaaaaaaaaaaaaaaaaa"),
            "First ambiguous task",
            AgentRole.Tester);
        var ambiguousB = new TaskSpec(
            new TaskId("feedfacebbbbbbbbbbbbbbbbbbbbbbbb"),
            "Second ambiguous task",
            AgentRole.Reviewer);
        var target = kernel.CreateGoal(
            new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Target task query goal",
            [first, second, ambiguousA, ambiguousB]);
        var other = kernel.CreateGoal(
            new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"),
            "Unrelated goal",
            [new TaskSpec(new TaskId("33333333cccccccccccccccccccccccc"), "Unrelated task", AgentRole.Developer)]);
        return new SeedData(kernel, target, other, first);
    }

    private static string ExecutePersistent(
        IReadOnlyList<string> args,
        InMemoryTransactionalStateRepository repository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var localCurrentGoal = currentGoal;
        var changed = true;
        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            args,
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref profiles,
            ref localCurrentGoal));
        Xunit.Assert.False(changed);
        currentGoal = localCurrentGoal;
        return output;
    }

    private static string ExecuteLegacy(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel seed,
        OrchestratorWorkspace workspace,
        GoalId? currentGoalId,
        out Goal? currentGoal)
    {
        var kernel = AgentOrchestratorKernel.FromSnapshot(seed.ExportSnapshot());
        Goal? localCurrentGoal = currentGoalId is null ? null : kernel.GetGoal(currentGoalId);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var changed = true;
        var output = CaptureConsole(() => changed = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref profiles,
            ref localCurrentGoal));
        Xunit.Assert.False(changed);
        currentGoal = localCurrentGoal;
        return output;
    }

    private static Exception CaptureLegacyException(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel seed,
        OrchestratorWorkspace workspace,
        GoalId currentGoalId)
    {
        var exception = Xunit.Record.Exception(() => ExecuteLegacy(
            args,
            seed,
            workspace,
            currentGoalId,
            out _));
        return Xunit.Assert.IsAssignableFrom<Exception>(exception);
    }

    private sealed record SeedData(
        AgentOrchestratorKernel Kernel,
        Goal Target,
        Goal Other,
        TaskSpec First);

    private sealed class QueryOnlyStateRepository(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        private readonly AgentOrchestratorKernel _kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            var ids = goalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var snapshot = _kernel.ExportSnapshot();
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Where(goal => ids.Contains(goal.Id)).ToArray(),
                HumanInputRequests = snapshot.HumanInputRequests
                    .Where(request => ids.Contains(request.GoalId))
                    .ToArray()
            }));
        }

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(_kernel.Goals
                .Select(goal => new GoalSummary(
                    goal.Id.Value,
                    goal.Status.ToString(),
                    goal.Objective,
                    DateTimeOffset.UtcNow.ToString("O")))
                .ToArray());
    }
}
