using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Unique real stores/worktrees; the collection protects console and environment use.
// Save callbacks are deterministic event gates at the actual runner persistence calls.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsGraphRepairWriterFamilies : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task SupervisorApplySafe_AppendBeforeSave_FailsLoudlyAndPreservesGraph()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Recover stalled worker", AgentRole.Planner);
            var goal = kernel.CreateGoal("Supervisor scoped save", [task]);
            var primary = SubscriptionPlanner("primary-planner", "Primary Planner");
            var alternate = SubscriptionPlanner("alternate-planner", "Alternate Planner");
            IReadOnlyList<AgentDefinition> agents = [primary, alternate];
            kernel.ActivateGoal(goal.Id, agents);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "fixture", root, DateTimeOffset.UtcNow));
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord("fixture", root, 1, "",
                "Background dispatch made no observable progress before the stall timeout; wrapper heartbeat state=running.", DateTimeOffset.UtcNow));
            await repository.SaveAsync(kernel);
            var proxy = SaveGate.Wrap(repository, out var gate);
            TaskId? inserted = null;
            gate.BeforeSave = () => inserted = Append(repository, goal.Id);
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = goal;
            Exception? failure = null;
            CaptureConsole(() => failure = Record.Exception(() => CliPersistentStateRunner.ExecuteCommand(
                ["supervisor", goal.Id.Value, "--apply-safe", "--autonomy", "safe-auto"], proxy, workspace,
                ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
            var durable = await repository.LoadAsync();
            Assert.True(inserted is not null && durable.GetGoal(goal.Id).Tasks.Any(item => item.Id == inserted),
                "Concurrent append must survive the actual writer-family checkpoint and reload.");
            Assert.Equal(1, gate.Interleavings);
            Assert.NotNull(failure);
            Assert.Contains("COMMAND_CHECKPOINT_REJECTED", failure.Message);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("goal-recovery")]
    [Xunit.InlineData("readiness-repair")]
    [Xunit.InlineData("next")]
    [Xunit.InlineData("startup")]
    public async Task ActualCaller_AppendBeforeSave_PreservesAuthoritativeGraph(string command)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Stale terminal repair", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var other = kernel.CreateGoal("Independent terminal repair", [new TaskSpec(TaskId.New(), "Other work", AgentRole.Developer)]);
            var untouched = kernel.CreateGoal("Independent request", [new TaskSpec(TaskId.New(), "Unrelated work", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(other.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(untouched.Id, AgentCatalog.Default().Agents);
            kernel = WithGoalStatus(WithGoalStatus(kernel, goal.Id, GoalStatus.Completed), other.Id, GoalStatus.Completed);
            await repository.SaveAsync(kernel);
            var proxy = SaveGate.Wrap(repository, out var gate);
            string? authoritative = null;
            TaskId? inserted = null;
            string? untouchedState = null;
            gate.BeforeSave = () =>
            {
                // Store mutation models the independent graph writer at the old holder's save.
                inserted = Append(repository, goal.Id);
                authoritative = TaskGraphIdentity.Compute(repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult()!);
                repository.TransactAsync((current, _) =>
                {
                    current.RequestHumanInput(untouched.Id, untouched.Tasks[0].Id, "Concurrent unrelated question");
                    return Task.FromResult((true, true));
                }).GetAwaiter().GetResult();
                untouchedState = State(repository.LoadAsync().GetAwaiter().GetResult(), untouched.Id);
            };
            var args = command == "startup"
                ? new[] { "conduct", "--loop", "--max-iterations", "0" }
                : new[] { command, goal.Id.Value };
            Exception? failure = null;
            CaptureConsole(() => CaptureConsoleError(() => failure = Record.Exception(() => Execute(args, proxy, workspace))));

            var durable = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
            Assert.True(inserted is not null && durable.GetGoal(goal.Id).Tasks.Any(task => task.Id == inserted),
                "Concurrent append must survive the actual writer-family checkpoint and reload.");
            Assert.Equal(authoritative, TaskGraphIdentity.Compute(durable.GetGoal(goal.Id)));
            Assert.Equal(GoalStatus.Active, durable.GetGoal(goal.Id).Status);
            Assert.Equal(1, gate.Interleavings);
            Assert.Equal(untouchedState, State(durable, untouched.Id));
            if (command == "startup")
            {
                Assert.Null(failure);
                Assert.Equal(WorkTaskStatus.Cancelled, durable.GetGoal(other.Id).Tasks[0].Status);
                var attention = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                    .ListForGoalIdsAsync(new HashSet<string>([goal.Id.Value]));
                Assert.Contains(attention, item => item.CorrelationKey == $"startup-checkpoint-conflict:{goal.Id.Value}" &&
                    item.Body.Contains("submitted=", StringComparison.Ordinal) && item.Body.Contains("stored=", StringComparison.Ordinal));
            }
            else
            {
                Assert.NotNull(failure);
                Assert.Contains("COMMAND_CHECKPOINT_REJECTED", failure.Message);
                Assert.Equal(WorkTaskStatus.Assigned, durable.GetGoal(other.Id).Tasks[0].Status);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "goal-mark-landed")]
    [Xunit.InlineData(false, "goal-mark-landed")]
    [Xunit.InlineData(true, "startup")]
    [Xunit.InlineData(false, "startup")]
    public async Task LandingOwner_Conflict_PreservesEvidenceAndGuardsRecovery(bool append, string command)
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanup = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Land verified candidate", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            cleanup = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, goal.Tasks[0].Id, WorkTaskStatus.Completed, "Done");
            kernel.RecordTaskVerification(goal.Id, goal.Tasks[0].Id,
                new TaskVerificationRecord("fixture", root, 0, "passed", "", DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--no-ff", "-m", $"Integrate goal/{goal.Id.Value[..8]}", GoalWorktrees.BranchName(goal.Id));
            await repository.SaveAsync(kernel);
            var covered = TaskGraphIdentity.Compute(goal);
            var proxy = SaveGate.Wrap(repository, out var gate);
            TaskId? inserted = null;
            gate.BeforeSave = () =>
            {
                var journal = GoalOperationJournal.Read(root, goal.Id);
                var evidenceGraph = command == "startup"
                    ? GoalOperationJournal.TryGetLatestTerminalDisposition(journal)?.TaskGraphIdentity
                    : GoalOperationJournal.TryGetLatestLandingIntent(journal)?.TaskGraphIdentity;
                Assert.Equal(covered, evidenceGraph); // Durable evidence precedes state save.
                if (append) inserted = Append(repository, goal.Id);
                else repository.TransactAsync((current, _) =>
                {
                    current.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "independent-progress");
                    return Task.FromResult((true, true));
                }).GetAwaiter().GetResult();
            };
            Exception? failure = null;
            var args = command == "startup" ? new[] { "conduct", "--loop", "--max-iterations", "0" }
                : new[] { "goal-mark-landed", goal.Id.Value, "--confirm-goal-mark-landed" };
            CaptureConsole(() => CaptureConsoleError(() => failure = Record.Exception(() => Execute(args, proxy, workspace))));
            var durable = await repository.LoadAsync();
            if (append)
                Assert.True(durable.GetGoal(goal.Id).Tasks.Any(task => task.Id == inserted),
                    "Concurrent append must survive the actual writer-family checkpoint and reload.");
            if (command == "startup") Assert.Null(failure);
            else
            {
                Assert.NotNull(failure);
                Assert.Contains("COMMAND_CHECKPOINT_REJECTED", failure.Message);
            }
            Assert.NotEqual(GoalStatus.Completed, durable.GetGoal(goal.Id).Status);
            Assert.Equal(1, gate.Interleavings);
            Assert.Equal(covered, GoalOperationJournal.TryGetLatestTerminalDisposition(GoalOperationJournal.Read(root, goal.Id))!.TaskGraphIdentity);
            var head = RunGitOutput(root, "rev-parse", "HEAD");

            // Actual supported recovery, using the persisted journal rather than repeating Git landing.
            CaptureConsole(() => Execute(["goal-recovery", goal.Id.Value], repository, workspace));
            var recovered = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
            Assert.Equal(head, RunGitOutput(root, "rev-parse", "HEAD"));
            if (append)
            {
                Assert.Equal(GoalStatus.Active, recovered.GetGoal(goal.Id).Status);
                Assert.Contains(recovered.GetGoal(goal.Id).Tasks, task => task.Id == inserted && task.Status == WorkTaskStatus.Pending);
            }
            else
            {
                Assert.Equal(GoalStatus.Completed, recovered.GetGoal(goal.Id).Status);
                Assert.Contains(recovered.GetGoal(goal.Id).Timeline, entry => entry.Message == "independent-progress");
            }
        }
        finally { CleanupAcceptanceRepository(root, cleanup); }
    }

    [Xunit.Fact]
    public async Task OwnedCheckpoint_RepeatedSaves_CarriesRequestsAndRejectsUnownedChanges()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var loaded = new AgentOrchestratorKernel();
            var goal = loaded.CreateGoal("Target", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var other = loaded.CreateGoal("Other", [new TaskSpec(TaskId.New(), "Independent", AgentRole.Developer)]);
            loaded.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            loaded.ActivateGoal(other.Id, AgentCatalog.Default().Agents);
            await repository.SaveAsync(loaded);
            var checkpoint = new CliOwnedGoalCheckpoint(repository, loaded, goal.Id);
            loaded.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "first");
            checkpoint.Persist(loaded);
            var request = loaded.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "Target question");
            checkpoint.Persist(loaded);
            checkpoint.Persist(loaded); // The invocation's own saves cannot conflict.
            var saved = await repository.LoadAsync();
            Assert.Contains(saved.HumanInputRequests, item => item.Id == request.Id);
            Assert.Contains(saved.GetGoal(goal.Id).Timeline, item => item.Message == "first");
            var before = JsonSerializer.Serialize(saved.ExportSnapshot());
            loaded.RecordTaskNote(goal.Id, goal.Tasks[0].Id, "must-not-save");
            loaded.RequestHumanInput(other.Id, other.Tasks[0].Id, "Unowned question");
            Assert.Contains("non-target", Assert.Throws<InvalidOperationException>(() => checkpoint.Persist(loaded)).Message);
            Assert.Equal(before, JsonSerializer.Serialize((await repository.LoadAsync()).ExportSnapshot()));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static TaskId Append(SqliteOrchestratorStateRepository repository, GoalId id) =>
        repository.TransactAsync((current, _) =>
        {
            var snapshot = current.ExportGoalSnapshot(id);
            current.ReplaceGoalWithSnapshot(snapshot with { Status = GoalStatus.Active });
            var added = current.AddTask(id, AgentRole.Tester, "Appended independent verification");
            return Task.FromResult((true, added.Id));
        }).GetAwaiter().GetResult();

    private static string State(AgentOrchestratorKernel kernel, GoalId id) => JsonSerializer.Serialize(
        new GoalStateSnapshot(kernel.ExportGoalSnapshot(id), kernel.ExportSnapshot().HumanInputRequests
            .Where(request => request.GoalId == id.Value).ToArray()));

    private static bool Execute(string[] args, ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CliPersistentStateRunner.ExecuteCommand(args, repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current);
    }

    public class SaveGate : DispatchProxy
    {
        private IOrchestratorStateOutboxRepository _repository = null!;
        public Action? BeforeSave { get; set; }
        public int Interleavings { get; private set; }

        public static IOrchestratorStateOutboxRepository Wrap(IOrchestratorStateOutboxRepository repository, out SaveGate gate)
        {
            var proxy = Create<IOrchestratorStateOutboxRepository, SaveGate>();
            gate = (SaveGate)proxy;
            gate._repository = repository;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (method.Name == nameof(IOrchestratorStateRepository.SaveAsync))
                throw new InvalidOperationException("Relevant mutating route reached unsafe raw replacement.");
            // Both arms share the same interleaving, including the raw-capture negative control.
            if (method.Name is nameof(ITransactionalOrchestratorStateRepository.SaveGoalSnapshotsWithMergeAsync) or
                nameof(ITransactionalOrchestratorStateRepository.TransactGoalStateAsync) && BeforeSave is { } before)
            {
                BeforeSave = null;
                Interleavings++;
                before();
            }
            try { return method.Invoke(_repository, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
