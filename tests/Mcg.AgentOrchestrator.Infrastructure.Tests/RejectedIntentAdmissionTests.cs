using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns a unique directory and SQLite stores; no processes or shared state.
public sealed class RejectedIntentAdmissionTests
{
    [Xunit.Fact]
    public async Task RejectedRoute_RunningDispatchContinues_AndRecordsOneEvent()
    {
        using var scenario = new Scenario();
        var reference = scenario.Prepare();
        scenario.SetStatus(WorkTaskStatus.Running);
        var actions = 0;
        var observed = new List<TaskIntentAdmissionEvent>();
        TaskIntentAdmission.ExecuteAuthorized(reference, "initial", () => actions++, observed.Add);
        var intent = await scenario.EnqueueRoute();

        // Use production adjudication, including enqueue's revision bump and rejection completion.
        var result = scenario.Coordinator.ExecutePending(scenario.Kernel, scenario.Goal);
        Xunit.Assert.True(result.RejectedAdjudication);
        Xunit.Assert.False(result.MutatedGoalState);
        var rejected = await scenario.Store.GetAsync(intent.Id);
        Xunit.Assert.NotNull(rejected);
        Xunit.Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Xunit.Assert.Contains("task-not-retryable", rejected.Outcome);
        Xunit.Assert.Equal(WorkTaskStatus.Running, scenario.Task.Status);
        Xunit.Assert.Equal(reference.Version + 1, scenario.CurrentVersion);
        Xunit.Assert.Equal(reference, scenario.Store.GetPreparedAdmission(
            reference.GoalId, reference.TaskId, scenario.Now));

        TaskIntentAdmission.ExecuteAuthorized(reference, "after-rejection", () => actions++, observed.Add);
        TaskIntentAdmission.ExecuteAuthorized(reference, "repeat-checkpoint", () => actions++, observed.Add);

        Xunit.Assert.Equal(3, actions);
        Xunit.Assert.Null(scenario.Store.GetSuppression("after-rejection"));
        Xunit.Assert.Null(scenario.Store.GetSuppression("repeat-checkpoint"));
        var continued = Xunit.Assert.Single(observed);
        Xunit.Assert.Equal("continued", continued.Kind);
        Xunit.Assert.Equal(intent.Id, continued.IntentId);
        Xunit.Assert.Equal("Rejected", continued.IntentStatus);
        Xunit.Assert.Equal(rejected.Outcome, continued.Outcome);
        Xunit.Assert.Equal(reference.GoalId, continued.GoalId);
        Xunit.Assert.Equal(reference.TaskId, continued.TaskId);
        Xunit.Assert.Equal(reference.DispatchedAt, continued.DispatchedAt);
        Xunit.Assert.Equal(reference.Version, continued.PreparedVersion);

        // Inspect the durable structured event, not only the in-process observer.
        var events = await new SqliteRunEventStore(scenario.Workspace.RunEventStorePath)
            .ReadSinceAsync(goalId: reference.GoalId);
        var stored = Xunit.Assert.Single(events, item => item.EventType == "task-intent.dispatch");
        Xunit.Assert.Equal("continued", stored.Operation);
        Xunit.Assert.Equal("Rejected", stored.Status);
        Xunit.Assert.Equal(continued, JsonSerializer.Deserialize<TaskIntentAdmissionEvent>(stored.PayloadJson!));

        // A new launch using the original preparation is released too. The sentinel avoids
        // creating a worker while proving that admission reached the launch callback.
        Xunit.Assert.Throws<LaunchReached>(() => TaskIntentAdmission.Launch(
            reference, "released-launch", () => throw new LaunchReached()));
        Xunit.Assert.Null(scenario.Store.GetSuppression("released-launch"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AppliedRoute_SuppressesRunningAndLaunch_EvenBeforeLaterRejection(bool laterRejection)
    {
        using var scenario = new Scenario();
        scenario.SetStatus(WorkTaskStatus.Failed);
        var reference = scenario.Prepare();
        var actions = 0;
        TaskIntentAdmission.ExecuteAuthorized(reference, "initial", () => actions++);
        var appliedIntent = await scenario.EnqueueRoute();
        var appliedResult = scenario.Coordinator.ExecutePending(scenario.Kernel, scenario.Goal);
        Xunit.Assert.True(appliedResult.MutatedGoalState);
        scenario.Coordinator.CompletePersisted([scenario.Goal.Id]);
        var applied = await scenario.Store.GetAsync(appliedIntent.Id);
        Xunit.Assert.NotNull(applied);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, applied.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, scenario.Task.Status);
        Xunit.Assert.Equal(RetryCause.NewSourceFinding, scenario.Task.PendingRetryCause);
        Xunit.Assert.Equal(Scenario.Feedback, scenario.Task.AcceptedRetryFeedback?.Message);

        if (laterRejection)
        {
            scenario.SetStatus(WorkTaskStatus.Running);
            var later = await scenario.EnqueueRoute();
            Xunit.Assert.True(scenario.Coordinator.ExecutePending(scenario.Kernel, scenario.Goal).RejectedAdjudication);
            var rejected = await scenario.Store.GetAsync(later.Id);
            Xunit.Assert.NotNull(rejected);
            Xunit.Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
            Xunit.Assert.Contains("task-not-retryable", rejected.Outcome);
        }
        Xunit.Assert.Equal(reference.Version + (laterRejection ? 2 : 1), scenario.CurrentVersion);

        var running = Xunit.Assert.Throws<TaskIntentDispatchSuppressedException>(() =>
            TaskIntentAdmission.ExecuteAuthorized(reference, "running", () => actions++));
        var launch = Xunit.Assert.Throws<TaskIntentDispatchSuppressedException>(() =>
            TaskIntentAdmission.Launch(reference, "launch", () => { actions++; throw new LaunchReached(); }));

        Xunit.Assert.Equal(1, actions);
        foreach (var denial in new[] { running, launch })
        {
            Xunit.Assert.Equal(appliedIntent.Id, denial.Receipt.IntentId);
            Xunit.Assert.Equal("Applied", denial.Receipt.IntentStatus);
            Xunit.Assert.Equal("task-admission-version-changed", denial.Receipt.Reason);
            Xunit.Assert.Equal(reference.Version, denial.Receipt.PreparedVersion);
            Xunit.Assert.Equal(scenario.CurrentVersion, denial.Receipt.TaskVersion);
            Xunit.Assert.Equal(denial.Receipt, scenario.Store.GetSuppression(denial.Receipt.DecisionKey));
        }
        var events = await new SqliteRunEventStore(scenario.Workspace.RunEventStorePath)
            .ReadSinceAsync(goalId: reference.GoalId);
        Xunit.Assert.DoesNotContain(events, item => item.EventType == "task-intent.dispatch");
    }

    [Xunit.Theory]
    [Xunit.InlineData(OperatorIntentStatus.Pending)]
    [Xunit.InlineData(OperatorIntentStatus.Claimed)]
    public async Task UnresolvedRoute_NewLaunchIsSuppressed_BeforeCallback(OperatorIntentStatus status)
    {
        using var scenario = new Scenario();
        var reference = scenario.Prepare();
        var intent = await scenario.EnqueueRoute();
        if (status == OperatorIntentStatus.Claimed)
        {
            var claimed = await scenario.Store.ClaimNextAsync(scenario.Goal.Id.Value, "test-owner");
            Xunit.Assert.NotNull(claimed);
            Xunit.Assert.Equal(intent.Id, claimed.Id);
        }
        var launches = 0;

        var denial = Xunit.Assert.Throws<TaskIntentDispatchSuppressedException>(() =>
            TaskIntentAdmission.Launch(reference, "unresolved-launch", () => { launches++; throw new LaunchReached(); }));

        Xunit.Assert.Equal(0, launches);
        Xunit.Assert.Equal(intent.Id, denial.Receipt.IntentId);
        Xunit.Assert.Equal(status.ToString(), denial.Receipt.IntentStatus);
        Xunit.Assert.Equal("pending-task-intent", denial.Receipt.Reason);
        Xunit.Assert.Equal(denial.Receipt, scenario.Store.GetSuppression("unresolved-launch"));
        Xunit.Assert.Equal(status, (await scenario.Store.GetAsync(intent.Id))!.Status);
        Xunit.Assert.Contains(scenario.Task.Id.Value, scenario.Store.ListHeldTaskIds(scenario.Goal.Id.Value));
    }

    private sealed class LaunchReached : Exception { }

    private sealed class Scenario : IDisposable, IClock
    {
        internal const string Feedback = "Repair the newly identified source finding.";
        private const long GoalStateVersion = 7;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-rejected-intent-" + Guid.NewGuid().ToString("N"));
        internal DateTimeOffset Now { get; } = new(2026, 10, 30, 6, 10, 11, TimeSpan.Zero);
        DateTimeOffset IClock.UtcNow => Now;
        internal AgentOrchestratorKernel Kernel { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal Goal Goal { get; }
        internal TaskSpec Task => Goal.Tasks.Single();
        internal SqliteOperatorIntentStore Store { get; }
        internal OperatorIntentCoordinator Coordinator { get; }
        internal long CurrentVersion => Store.CaptureAdmission(Goal.Id.Value, Task.Id.Value).Version;

        internal Scenario()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Kernel = new AgentOrchestratorKernel(this);
            AgentDefinition[] agents = [new(new AgentId("worker"), "Worker", AgentRole.Developer,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text,
                    SubscriptionMode.ApiKey, "medium"),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"))];
            Goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, agents, "Rejected intent admission");
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Rejected intent admission",
                ["Rejected intents release dispatch admission"], VerificationClass.TestVerifiable, [], []));
            Store = new SqliteOperatorIntentStore(Path.Combine(Workspace.OrchestratorDirectory,
                SqliteOperatorIntentStore.DatabaseFileName), Workspace.LogDirectory, utcNow: () => Now);
            Coordinator = new OperatorIntentCoordinator(Store, utcNow: () => Now,
                decisions: CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory),
                goalStateVersionResolver: _ => GoalStateVersion);
        }

        internal TaskIntentAdmissionReference Prepare()
        {
            var reference = Store.CaptureAdmission(Goal.Id.Value, Task.Id.Value) with { DispatchedAt = Now };
            Store.BindPreparedAdmission(reference, Now);
            return reference;
        }

        internal void SetStatus(WorkTaskStatus status) => Kernel.ReportTaskProgress(Goal.Id, Task.Id, status, "test setup");

        internal Task<OperatorIntentRecord> EnqueueRoute()
        {
            var id = Guid.NewGuid().ToString("N");
            var payload = new AdjudicateOperatorIntentPayload("route", Feedback, ["source-finding-receipt"],
                GoalStateVersion, _root, Cause: nameof(RetryCause.NewSourceFinding));
            return Store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.Adjudicate,
                Goal.Id.Value, Task.Id.Value, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), [],
                "operator", "cli", "local-process", Now, ActorKind: OperatorActorKind.Human));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
