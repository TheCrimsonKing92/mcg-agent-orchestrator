using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsTaskQueries : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public void Collaborator_AcceptsReadCapabilityWithoutMutationAuthority()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            IOrchestratorStateQueries queries = new QueryOnlyStateRepository(seed.Kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = seed.Target;

            var output = CaptureConsole(() => CliTaskQueryCommand.Execute(
                ["task", "1"],
                queries,
                OrchestratorWorkspace.ForDirectory(root),
                new InMemoryModelProviderRegistry([]),
                channel: null,
                ref agents,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains(seed.First.Description, output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ExplicitTask_UsesOnlyTargetedRead()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            var repository = new ProbeStateRepository(seed.Kernel);
            Goal? currentGoal = seed.Other;

            var output = ExecutePersistent(
                ["task", "--goal", seed.Target.Id.Value[..8], "1"],
                repository,
                OrchestratorWorkspace.ForDirectory(root),
                ref currentGoal);

            Xunit.Assert.Contains(seed.First.Description, output, StringComparison.Ordinal);
            Xunit.Assert.Equal(seed.Target.Id, currentGoal!.Id);
            AssertQueryOnly(repository, seed.Target.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void TaskQuery_DoesNotUseMutationOrOutboxCapabilities()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
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
                OrchestratorWorkspace.ForDirectory(root),
                ref currentGoal);

            Xunit.Assert.Contains(seed.First.Description, output, StringComparison.Ordinal);
            AssertQueryOnly(repository, seed.Target.Id);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.True(repository.HasOutboxMessage(outboxMessage.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void TaskAndTasks_PreserveOutputAndTargetSelection()
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
                var expected = ExecuteLegacyPersistentSemantics(
                    testCase.Args,
                    seed.Kernel,
                    workspace,
                    testCase.CurrentGoalId.Value,
                    out var expectedCurrentGoal);
                var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
                Goal? actualCurrentGoal = seed.Kernel.GetGoal(testCase.CurrentGoalId);

                var actual = ExecutePersistent(
                    testCase.Args,
                    repository,
                    workspace,
                    ref actualCurrentGoal);

                Xunit.Assert.Equal(expected, actual);
                Xunit.Assert.Equal(expectedCurrentGoal!.Id, actualCurrentGoal!.Id);
                AssertQueryOnly(repository, expectedCurrentGoal.Id);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void TaskErrors_PreserveExceptionContracts()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var cases = new (string[] Args, int ExpectedGoalLoads)[]
            {
                (new[] { "task", "99" }, 1),
                (["task", "11111111"], 1),
                (["task", "deadbeef"], 1),
                (["task", "feedface"], 1),
                (["task", "abc", "1"], 2),
                (["tasks", "status"], 1),
                (["tasks", "unknown", "value"], 1)
            };

            foreach (var testCase in cases)
            {
                var expected = CaptureLegacyException(testCase.Args, seed.Kernel, workspace, seed.Target.Id.Value);
                var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
                Goal? currentGoal = seed.Target;

                var actual = CapturePersistentException(testCase.Args, repository, workspace, ref currentGoal);

                Xunit.Assert.Equal(expected.GetType(), actual.GetType());
                Xunit.Assert.Equal(expected.Message, actual.Message);
                Xunit.Assert.Equal(seed.Target.Id, currentGoal!.Id);
                AssertNoMutation(repository);
                Xunit.Assert.Equal(testCase.ExpectedGoalLoads, repository.LoadGoalsCount);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InvalidFlags_PrecedeGoalResolutionAndReads()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var args = new[] { "task", "--goal", "missing0", "1", "--bogus" };
            var expected = CaptureLegacyException(args, seed.Kernel, workspace, seed.Target.Id.Value);
            var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
            Goal? currentGoal = seed.Target;

            var actual = CapturePersistentException(args, repository, workspace, ref currentGoal);

            Xunit.Assert.Equal(expected.GetType(), actual.GetType());
            Xunit.Assert.Equal(expected.Message, actual.Message);
            Xunit.Assert.Equal(seed.Target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            AssertNoMutation(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ExplicitGoalErrors_PreserveContractsWithoutMutation()
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
                var expected = CaptureLegacyException(args, seed.Kernel, workspace, seed.Target.Id.Value);
                var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
                Goal? currentGoal = seed.Target;

                var actual = CapturePersistentException(args, repository, workspace, ref currentGoal);

                Xunit.Assert.Equal(expected.GetType(), actual.GetType());
                Xunit.Assert.Equal(expected.Message, actual.Message);
                AssertNoMutation(repository);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ImplicitGoal_UsesCurrentOrLatestWithoutMutation()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var seed = CreateSeed();
            var staleKernel = new AgentOrchestratorKernel();
            var staleGoal = staleKernel.CreateGoal(
                new GoalId("stale000dddddddddddddddddddddddd"),
                "Stale current goal",
                [new TaskSpec(new TaskId("staletaskddddddddddddddddddddddd"), "Stale task", AgentRole.Developer)]);

            foreach (var suppliedCurrentGoal in new Goal?[] { null, staleGoal, seed.Target })
            {
                var expected = ExecuteLegacyPersistentSemantics(
                    ["tasks"],
                    seed.Kernel,
                    workspace,
                    suppliedCurrentGoal?.Id.Value,
                    out var expectedCurrentGoal);
                var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
                var actualCurrentGoal = suppliedCurrentGoal;

                var actual = ExecutePersistent(["tasks"], repository, workspace, ref actualCurrentGoal);

                Xunit.Assert.Equal(expected, actual);
                Xunit.Assert.Equal(expectedCurrentGoal!.Id, actualCurrentGoal!.Id);
                AssertQueryOnly(repository, expectedCurrentGoal.Id);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ImplicitGoal_SkipsUnavailableNewestMetadata()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
            repository.UnavailableGoalIds.Add(seed.Other.Id.Value);
            Goal? currentGoal = null;

            var output = ExecutePersistent(
                ["tasks"],
                repository,
                OrchestratorWorkspace.ForDirectory(root),
                ref currentGoal);

            Xunit.Assert.Contains(seed.First.Description, output, StringComparison.Ordinal);
            Xunit.Assert.Equal(seed.Target.Id, currentGoal!.Id);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(2, repository.LoadGoalsCount);
            Xunit.Assert.Equal([seed.Target.Id.Value], repository.LoadedGoalIds);
            AssertNoMutation(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void EmptyStore_QueryErrorsWithoutEnteringMutationRoute()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var empty = new AgentOrchestratorKernel();
            var cases = new[]
            {
                new[] { "tasks" },
                ["task"]
            };
            var staleKernel = new AgentOrchestratorKernel();
            var staleGoal = staleKernel.CreateGoal(
                new GoalId("stale000dddddddddddddddddddddddd"),
                "Stale current goal",
                [new TaskSpec(new TaskId("staletaskddddddddddddddddddddddd"), "Stale task", AgentRole.Developer)]);

            foreach (var args in cases)
            {
                foreach (var suppliedCurrentGoal in new Goal?[] { null, staleGoal })
                {
                    var expected = CaptureLegacyException(
                        args,
                        empty,
                        workspace,
                        suppliedCurrentGoal?.Id.Value);
                    var repository = new ProbeStateRepository(empty) { ThrowOnOutbox = true };
                    var currentGoal = suppliedCurrentGoal;

                    var actual = CapturePersistentException(args, repository, workspace, ref currentGoal);

                    Xunit.Assert.Equal(expected.GetType(), actual.GetType());
                    Xunit.Assert.Equal(expected.Message, actual.Message);
                    Xunit.Assert.Equal(suppliedCurrentGoal?.Id, currentGoal?.Id);
                    Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
                    Xunit.Assert.Equal(0, repository.LoadGoalsCount);
                    AssertNoMutation(repository);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ImplicitError_PreservesNullCurrentGoalReference()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
            Goal? currentGoal = null;

            var exception = CapturePersistentException(
                ["task", "99"],
                repository,
                OrchestratorWorkspace.ForDirectory(root),
                ref currentGoal);

            Xunit.Assert.IsType<KeyNotFoundException>(exception);
            Xunit.Assert.Null(currentGoal);
            AssertNoMutation(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DisappearedTarget_FailsWithoutEnteringMutationRoute()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            var repository = new ProbeStateRepository(seed.Kernel)
            {
                DisappearSelectedGoals = true,
                ThrowOnOutbox = true
            };
            Goal? currentGoal = seed.Other;

            var exception = CapturePersistentException(
                ["task", "--goal", seed.Target.Id.Value[..8], "1"],
                repository,
                OrchestratorWorkspace.ForDirectory(root),
                ref currentGoal);

            Xunit.Assert.IsType<KeyNotFoundException>(exception);
            Xunit.Assert.Equal($"Goal '{seed.Target.Id.Value[..8]}' was not found.", exception.Message);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            AssertNoMutation(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MutationCommands_DoNotEnterTaskQueryRoute()
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
                var seed = CreateSeed();
                var repository = new ProbeStateRepository(seed.Kernel);
                Goal? currentGoal = seed.Target;

                if (args[0].Equals("add-task", StringComparison.Ordinal))
                {
                    var exception = CapturePersistentException(
                        args,
                        repository,
                        OrchestratorWorkspace.ForDirectory(root),
                        ref currentGoal);
                    Xunit.Assert.Equal("State mutation is not allowed for this test.", exception.Message);
                    Xunit.Assert.Equal(1, repository.MutationAttempts);
                }
                else
                {
                    var output = ExecutePersistent(
                        args,
                        repository,
                        OrchestratorWorkspace.ForDirectory(root),
                        ref currentGoal);
                    Xunit.Assert.Contains("Operator intent queued:", output, StringComparison.Ordinal);
                    Xunit.Assert.Contains($"verb={args[0]}", output, StringComparison.Ordinal);
                    Xunit.Assert.Equal(0, repository.MutationAttempts);
                }

                Xunit.Assert.True(repository.ListOutboxMessagesCount >= 1);
                Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
                Xunit.Assert.Equal(0, repository.LoadGoalsCount);
                Xunit.Assert.Equal(0, repository.FullLoadAttempts);
                Xunit.Assert.Equal(0, repository.SaveAttempts);
                Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
                Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void Help_DoesNotReadOrMutateState()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = CreateSeed();
            var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
            Goal? currentGoal = seed.Target;

            var output = ExecutePersistent(
                ["task", "--help"],
                repository,
                OrchestratorWorkspace.ForDirectory(root),
                ref currentGoal);

            Xunit.Assert.Contains("Usage: task [options]", output, StringComparison.Ordinal);
            Xunit.Assert.Contains("Run this operator command.", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            AssertNoMutation(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SeedData CreateSeed()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(
            new TaskId("taskone1aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Inspect target task",
            AgentRole.Developer);
        var second = new TaskSpec(
            new TaskId("tasktwo2bbbbbbbbbbbbbbbbbbbbbbbb"),
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
            [new TaskSpec(new TaskId("othertskcccccccccccccccccccccccc"), "Unrelated task", AgentRole.Developer)]);
        return new SeedData(kernel, target, other, first);
    }

    private static string ExecutePersistent(
        IReadOnlyList<string> args,
        ProbeStateRepository repository,
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

    private static Exception CapturePersistentException(
        IReadOnlyList<string> args,
        ProbeStateRepository repository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var localCurrentGoal = currentGoal;
        var exception = Xunit.Record.Exception(() => CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args,
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([]),
            ref profiles,
            ref localCurrentGoal)));
        currentGoal = localCurrentGoal;
        return Xunit.Assert.IsAssignableFrom<Exception>(exception);
    }

    private static string ExecuteLegacyPersistentSemantics(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel seed,
        OrchestratorWorkspace workspace,
        string? currentGoalId,
        out Goal? currentGoal)
    {
        var kernel = AgentOrchestratorKernel.FromSnapshot(seed.ExportSnapshot());
        Goal? localCurrentGoal = kernel.Goals.FirstOrDefault(goal =>
            goal.Id.Value.Equals(currentGoalId, StringComparison.OrdinalIgnoreCase));
        localCurrentGoal ??= OrchestratorEntityResolver.GetLatestGoal(kernel);
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
        string? currentGoalId)
    {
        var exception = Xunit.Record.Exception(() => ExecuteLegacyPersistentSemantics(
            args,
            seed,
            workspace,
            currentGoalId,
            out _));
        return Xunit.Assert.IsAssignableFrom<Exception>(exception);
    }

    private static void AssertQueryOnly(ProbeStateRepository repository, GoalId expectedGoalId)
    {
        Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal([expectedGoalId.Value], repository.LoadedGoalIds);
        AssertNoMutation(repository);
    }

    private static void AssertNoMutation(ProbeStateRepository repository)
    {
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
        Xunit.Assert.Equal(0, repository.MutationAttempts);
        Xunit.Assert.Equal(0, repository.SaveAttempts);
        Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
    }

    private sealed record SeedData(
        AgentOrchestratorKernel Kernel,
        Goal Target,
        Goal Other,
        TaskSpec First);
}
