using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsGraphRepairConductCheckpoint : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AcceptanceOwner_ProgressSave_AdvancesNativeCriticalBaseline(bool externalProgress)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Checkpoint progress", AgentRole.Planner);
        var goal = kernel.CreateGoal("Acceptance baseline ownership", [task]);
        var other = kernel.CreateGoal("Unrelated goal", [new(TaskId.New(), "Independent", AgentRole.Developer)]);
        await repository.SaveAsync(kernel);
        var initial = (await repository.LoadGoalAsync(goal.Id))!;
        kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([initial], []));
        var proxy = DispatchProxy.Create<IOrchestratorStateOutboxRepository, CheckpointProbe>();
        var probe = (CheckpointProbe)proxy;
        probe.Repository = repository;
        probe.Workspace = workspace;
        probe.TargetGoalId = goal.Id;
        var checkpoints = new CliAcceptanceCheckpoint(proxy, workspace, kernel, goal.Id, initial);
        kernel.RecordTaskNote(goal.Id, task.Id, "durable progress before critical checkpoint");
        var command = Task.Run(() => Record.Exception(() =>
        {
            if (externalProgress)
            {
                var result = proxy.SaveGoalSnapshotsWithMergeAsync([
                    new(initial, kernel.ExportGoalSnapshot(goal.Id), [])
                ]).GetAwaiter().GetResult().Single();
                // The runner also receives snapshots from store-side dispatch operations.
                checkpoints.RecordDurableBaseline(result.PersistedSnapshot!);
            }
            else
                checkpoints.Persist(kernel);

            kernel.RecordTaskNote(goal.Id, task.Id, "critical progress");
            checkpoints.PersistCritical(kernel, [goal.Id]);
        }));
        try
        {
            var first = await Task.WhenAny(probe.ProgressSaved.Task, command).WaitAsync(TimeSpan.FromMinutes(2));
            if (first == command)
                Assert.Fail($"Acceptance owner exited before durable progress: {await command}");
            await probe.ProgressSaved.Task;
            var durable = await repository.LoadGoalAsync(goal.Id);
            Assert.NotEqual(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(durable));
            string? otherState = null;
            await repository.TransactAsync((stored, _) =>
            {
                stored.RequestHumanInput(other.Id, other.Tasks[0].Id, "Unrelated request during acceptance progress");
                otherState = State(stored, other.Id);
                return Task.FromResult((true, true));
            });
            probe.Resume.TrySetResult();
            var failure = await command.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(1, probe.ProgressSaves);
            Assert.Equal(1, probe.CriticalChecks);
            Assert.True(probe.BaselineMatched, "One invocation must advance its baseline before the native critical checkpoint.");
            Assert.IsType<DispatchCheckpointConflictException>(failure); // Deliberate stop at the native seam.
            Assert.Equal(JsonSerializer.Serialize(durable), JsonSerializer.Serialize(await repository.LoadGoalAsync(goal.Id)));
            Assert.Equal(otherState, State(await repository.LoadAsync(), other.Id));
        }
        finally
        {
            probe.Resume.TrySetResult();
            await command.WaitAsync(TimeSpan.FromMinutes(2));
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public async Task SingleGoalWatch_ProgressSave_AdvancesFollowingNativeCriticalBaseline()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        EnsureGitRepository(root);
        RunGit(root, "branch", "-M", "main");
        RunGit(root, "add", ".agents/skills");
        RunGit(root, "commit", "-m", "fixture skills");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Exercise checkpoint ownership", AgentRole.Planner);
        var goal = kernel.CreateGoal("Single-goal baseline integration", [task]);
        var other = kernel.CreateGoal("Unrelated goal", [new(TaskId.New(), "Independent", AgentRole.Developer)]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Checkpoint fixture", ["Preserve checkpoint baseline"],
            VerificationClass.TestVerifiable, [], []));
        IReadOnlyList<AgentDefinition> agents = [SubscriptionPlanner("safe-profile", "Fixture Planner")];
        kernel.ActivateGoal(goal.Id, agents);
        var profiles = new WorkerProfileCatalog([new WorkerProfile("safe-profile",
            "Write-Output {subscriptionModelName}; Write-Output {promptPath}")]);
        GoalWorktrees.Ensure(root, goal.Id);
        await repository.SaveAsync(kernel);
        // Activation owns assignment. Resetting this task to Pending leaves no assigned
        // dispatch candidate; the watch loop cannot reach its critical start checkpoint.
        var planned = Assert.Single(SubscriptionPlanBuilder.Build(
            (await repository.LoadAsync()).GetGoal(goal.Id), agents, profiles).Items);
        Assert.True(planned.CanPrepare, $"Fixture Planner must be ready for native dispatch: {planned.Detail}");
        var proxy = DispatchProxy.Create<IOrchestratorStateOutboxRepository, CheckpointProbe>();
        var probe = (CheckpointProbe)proxy;
        probe.Repository = repository;
        probe.Workspace = workspace;
        probe.TargetGoalId = goal.Id;
        Goal? current = kernel.GetGoal(goal.Id);
        string[] args = ["conduct", goal.Id.Value, "--watch", "--policy", "Permissive"];
        Assert.True(CliPersistentStateRunner.IsConductLoop(args));
        Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(args));
        // The inbox prewalk must produce a real progress checkpoint before dispatch is eligible.
        // A ready goal with an existing worktree otherwise reaches the critical save first.
        Assert.False(CliPersistentStateRunner.ExecuteCommand(
            ["add-task", "Reviewer", "Review repaired graph"],
            repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current));
        var command = Task.Run(() => CliPersistentStateRunner.ExecuteCommand(
            args, proxy, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));
        string? otherState = null;
        try
        {
            // Surface early exit/fault instead of waiting for an event the command cannot emit.
            var first = await Task.WhenAny(probe.ProgressSaved.Task, command).WaitAsync(TimeSpan.FromMinutes(2));
            if (first == command)
            {
                await command;
                Assert.Fail("Single-goal watch exited before the repaired-graph progress checkpoint.");
            }
            await probe.ProgressSaved.Task;
            Assert.Equal(new[] { AgentRole.Planner, AgentRole.Reviewer },
                (await repository.LoadAsync()).GetGoal(goal.Id).Tasks.Select(item => item.RequiredRole));
            await repository.TransactAsync((stored, _) =>
            {
                stored.RequestHumanInput(other.Id, other.Tasks[0].Id, "Unrelated request while checkpoint is paused");
                otherState = State(stored, other.Id);
                return Task.FromResult((true, true));
            });
            probe.Resume.TrySetResult();
            await command.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(probe.ProgressSaves > 0, "Actual single-goal conduct did not persist a progress checkpoint.");
            Assert.True(probe.CriticalChecks > 0, "Actual single-goal conduct did not reach the native critical checkpoint.");
            Assert.True(probe.BaselineMatched, "One invocation must advance its baseline before the native critical checkpoint.");
            var restarted = await repository.LoadAsync();
            Assert.Equal(new[] { AgentRole.Planner, AgentRole.Reviewer },
                restarted.GetGoal(goal.Id).Tasks.Select(item => item.RequiredRole));
            Assert.Equal(otherState, State(restarted, other.Id));
            Assert.Empty(Directory.GetFiles(workspace.LogDirectory, "*.start-gate", SearchOption.AllDirectories));
        }
        finally
        {
            probe.Resume.TrySetResult();
            File.WriteAllText(workspace.ConductorStopFilePath, "fixture cleanup");
            await command.WaitAsync(TimeSpan.FromMinutes(2));
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static string State(AgentOrchestratorKernel kernel, GoalId id) => JsonSerializer.Serialize(new
    {
        Goal = kernel.ExportGoalSnapshot(id),
        Requests = kernel.ExportSnapshot().HumanInputRequests.Where(item => item.GoalId == id.Value).ToArray()
    });

    public class CheckpointProbe : DispatchProxy
    {
        public SqliteOrchestratorStateRepository Repository { get; set; } = null!;
        public OrchestratorWorkspace Workspace { get; set; } = null!;
        public GoalId TargetGoalId { get; set; }
        public TaskCompletionSource ProgressSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProgressSaves { get; private set; }
        public int CriticalChecks { get; private set; }
        public bool BaselineMatched { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ITransactionalOrchestratorStateRepository.CheckpointGoalSnapshotsAsync))
                return SaveContainedProgress((IReadOnlyCollection<GoalSnapshotSaveRequest>)args![0]!);
            if (targetMethod!.Name == nameof(ITransactionalOrchestratorStateRepository.SaveGoalSnapshotsWithMergeAsync))
            {
                var requests = (IReadOnlyCollection<GoalSnapshotSaveRequest>)args![0]!;
                if (requests.Any(request => request.RejectConflict))
                {
                    CriticalChecks++;
                    var request = Assert.Single(requests);
                    Assert.Equal(TargetGoalId.Value, request.Current.Id);
                    var stored = Repository.LoadGoalAsync(new GoalId(request.Current.Id)).GetAwaiter().GetResult()!;
                    BaselineMatched = ProgressSaves > 0 && JsonSerializer.Serialize(stored) == JsonSerializer.Serialize(request.Baseline);
                    File.WriteAllText(Workspace.ConductorStopFilePath, "native checkpoint reached; no fixture worker start");
                    // Deliberately stop at the real critical seam before any worker process starts.
                    return Task.FromResult<IReadOnlyList<GoalSnapshotSaveResult>>([
                        new(request.Current.Id, GoalSnapshotSaveDisposition.Skipped, stored, "fixture stop after baseline assertion", [])]);
                }
                return SaveProgress(requests);
            }
            try { return targetMethod.Invoke(Repository, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private async Task<IReadOnlyList<GoalSnapshotCheckpointResult>> SaveContainedProgress(
            IReadOnlyCollection<GoalSnapshotSaveRequest> requests)
        {
            var results = await Repository.CheckpointGoalSnapshotsAsync(requests);
            Assert.NotEmpty(results);
            Assert.All(results, result =>
            {
                Assert.True(result.IsDurable);
                Assert.NotNull(result.SaveResult);
                Assert.Equal(result.GoalId, result.SaveResult.GoalId);
            });
            await AssertCommittedProgress(requests, results.Select(result => result.SaveResult!).ToArray());
            await PauseAfterProgress();
            return results;
        }

        private async Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveProgress(IReadOnlyCollection<GoalSnapshotSaveRequest> requests)
        {
            var results = await Repository.SaveGoalSnapshotsWithMergeAsync(requests);
            await AssertCommittedProgress(requests, results);
            await PauseAfterProgress();
            return results;
        }

        private async Task AssertCommittedProgress(IReadOnlyCollection<GoalSnapshotSaveRequest> requests,
            IReadOnlyList<GoalSnapshotSaveResult> results)
        {
            Assert.NotEmpty(results);
            Assert.Equal(requests.Select(request => request.Current.Id), results.Select(result => result.GoalId));
            var reloaded = await Repository.LoadAsync();
            foreach (var result in results)
            {
                // A merge is a committed checkpoint too; containment alone can also wrap a skipped save.
                Assert.True(result.Disposition is GoalSnapshotSaveDisposition.Saved or GoalSnapshotSaveDisposition.Merged,
                    $"Progress checkpoint for {result.GoalId} must commit: {result.Disposition}: {result.Message}");
                Assert.NotNull(result.PersistedSnapshot);
                // Compare durable representations: kernel export normalizes empty gate obligations to null.
                var stored = await Repository.LoadGoalAsync(new GoalId(result.GoalId));
                Assert.NotNull(stored);
                Assert.Equal(JsonSerializer.Serialize(result.PersistedSnapshot),
                    JsonSerializer.Serialize(stored));
                Assert.NotNull(result.PersistedHumanInputRequests);
                Assert.Equal(JsonSerializer.Serialize(result.PersistedHumanInputRequests),
                    JsonSerializer.Serialize(reloaded.ExportSnapshot().HumanInputRequests
                        .Where(request => request.GoalId == result.GoalId).ToArray()));
            }
        }

        private async Task PauseAfterProgress()
        {
            if (++ProgressSaves == 1)
            {
                ProgressSaved.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
        }
    }
}
