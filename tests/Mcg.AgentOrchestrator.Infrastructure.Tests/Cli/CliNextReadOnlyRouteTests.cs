using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each fact owns its workspace and stores; console capture is async-local.
public sealed class CliNextReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private static readonly GoalId TargetId = new("abc10000aaaaaaaaaaaaaaaaaaaaaaaa");

    [Fact]
    public void Prefix_LoadsOnlyTargetWithoutWriterEffects_AndMatchesWriterNextActions()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, "Healthy next query");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(["next", "abc10000"], repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(TargetId, result.Goal?.Id);
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(1, repository.LoadGoalsCount);
            Assert.Equal([TargetId.Value], repository.LoadedGoalIds);
            AssertNoWriterEffects(repository);

            var writerRepository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var writerOutput = CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
                ["next", "abc10000"], writerRepository, workspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
                skipReadOnlyRoute: true)));

            Assert.Equal(0, writerRepository.SaveAttempts);
            Assert.Equal(0, writerRepository.MergeSaveAttempts);
            Assert.Equal(NextActionsSection(writerOutput), NextActionsSection(result.Output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("next", "abc10000")]
    [InlineData("next", "--full", "abc10000")]
    public void ConcreteSweepCommand_DeclinesWithoutWriterEffects(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Live dispatch", AgentRole.Developer);
            var goal = kernel.CreateGoal(TargetId, "Terminal live dispatch", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var startedAt = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, startedAt));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(Environment.ProcessId, "codex exec prompt.md", root,
                    Path.Combine(root, "stdout.log"), Path.Combine(root, "stderr.log"),
                    Path.Combine(root, "exit.txt"), startedAt, null, null));
            kernel = WithCompletedGoal(kernel);
            var diagnosis = TerminalGoalSweep.Diagnose(kernel, root, TargetId);
            Assert.Contains(diagnosis.Goals.Single().Blockers, blocker =>
                blocker.Kind == "terminal-live-dispatch" && blocker.Command == "refresh-dispatch abc10000 1");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(args, repository, workspace);

            Assert.False(result.Served);
            Assert.False(result.Changed);
            Assert.Empty(result.Output);
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(1, repository.LoadGoalsCount);
            Assert.Equal([TargetId.Value], repository.LoadedGoalIds);
            AssertNoWriterEffects(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("next", "abc10000")]
    [InlineData("next", "--full", "abc10000")]
    public async Task PersistedSweepBlocker_DeclinesWithoutWriterEffects(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, "Persisted sweep blocker");
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await store.RaiseAsync(CollaborationItemType.Decision, TargetId.Value,
                "Stale sweep blocker", "Needs writer reconciliation",
                $"terminal-sweep-blocker:{TargetId.Value}:stale");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(args, repository, workspace);

            Assert.False(result.Served);
            Assert.False(result.Changed);
            Assert.Empty(result.Output);
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(1, repository.LoadGoalsCount);
            Assert.Equal([TargetId.Value], repository.LoadedGoalIds);
            AssertNoWriterEffects(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TerminalTaskDesync_IsInspectedWithoutSweepRepair()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Assigned work", AgentRole.Developer);
            kernel.CreateGoal(TargetId, "Terminal task desync", [task]);
            kernel.ActivateGoal(TargetId, AgentCatalog.Default().Agents);
            kernel = WithCompletedGoal(kernel);
            Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(TargetId, task.Id).Status);
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(["next", "abc10000"], repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(GoalStatus.Completed, result.Goal?.Status);
            Assert.Equal(WorkTaskStatus.Assigned, result.Goal!.Tasks.Single().Status);
            Assert.DoesNotContain("SWEEP_REPAIR", result.Output, StringComparison.Ordinal);
            var items = CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory)
                .ListForGoalIdsAsync([TargetId.Value]).GetAwaiter().GetResult();
            Assert.Empty(items);
            AssertNoWriterEffects(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("next", "abc10000", "--autonomy", "conservative")]
    [InlineData("next", "abc10000", "--autonomy-policy", "conservative")]
    [InlineData("next", "--autonomy", "conservative")]
    [InlineData("next", "--autonomy-policy", "conservative")]
    [InlineData("next", "abc10000", "--full")]
    [InlineData("next", "abc10000", "extra")]
    [InlineData("next", "--help")]
    [InlineData("next", "-h")]
    [InlineData("next", "-prefix")]
    [InlineData("next", "")]
    [InlineData("next", " ")]
    public void OtherNextForms_KeepWriterRoute(params string[] args) =>
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    private static (bool Served, bool Changed, string Output, Goal? Goal) ExecuteReadOnly(
        string[] args, ProbeStateRepository repository, OrchestratorWorkspace workspace)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var served = false;
        var changed = false;
        var output = CaptureConsole(() => served = CliReadOnlyCommandRunner.TryExecute(
            args, repository, workspace, new InMemoryModelProviderRegistry([]), null,
            ref agents, ref profiles, ref currentGoal, out changed));
        return (served, changed, output, currentGoal);
    }

    private static void AssertNoWriterEffects(ProbeStateRepository repository)
    {
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.SaveAttempts);
        Assert.Equal(0, repository.MergeSaveAttempts);
        Assert.Equal(0, repository.ListOutboxMessagesCount);
        Assert.Equal(0, repository.OutboxClaimAttempts);
    }

    private static string NextActionsSection(string output)
    {
        Assert.Contains("Goal abc10000 ", output, StringComparison.Ordinal);
        var start = output.IndexOf($"Next actions:{Environment.NewLine}", StringComparison.Ordinal);
        Assert.True(start >= 0, "The next-actions section was not printed.");
        return output[start..];
    }

    private static AgentOrchestratorKernel WithCompletedGoal(AgentOrchestratorKernel kernel)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == TargetId.Value
                ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        });
    }
}
