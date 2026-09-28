using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundDispatchRunnerTestsOperatorCancelRequeueRace
{
    [Xunit.Theory]
    [Xunit.InlineData(OperatorIntentStatus.Pending)]
    [Xunit.InlineData(OperatorIntentStatus.Claimed)]
    [Xunit.InlineData(OperatorIntentStatus.Applied)]
    public void DeadProcessWithCancelIntentIsNotRequeued(OperatorIntentStatus status)
    {
        var (kernel, goal, task) = RunningTask();
        var store = new CancelDispatchIntentTestStore();
        store.Add(Intent(goal, task) with { Status = status });
        var refusals = new List<string>();
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false)
        {
            OperatorIntents = store,
            RequeueRefusalLog = refusals.Add
        };

        var recovered = runner.RequeueInterruptedDispatches(kernel);

        Xunit.Assert.Equal(0, recovered);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Message.Contains("Auto-requeued orphaned", StringComparison.Ordinal));
        Xunit.Assert.Contains(refusals, line => line.Contains("reason=operator-cancel-intent", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ProviderSessionRecordedAfterCancelIntentStillRefusesRequeue()
    {
        var (kernel, goal, task) = RunningTask();
        var store = new CancelDispatchIntentTestStore();
        store.Add(Intent(goal, task));
        kernel.RecordDispatchProviderSessionId(goal.Id, task.Id, "session-after-cancel");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false)
        {
            OperatorIntents = store,
            RequeueRefusalLog = _ => { }
        };

        Xunit.Assert.Equal(0, runner.RequeueInterruptedDispatches(kernel));
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    public void NoEffectiveIntentKeepsGenuineOrphanRecovery(bool rejected, bool olderProcess)
    {
        var (kernel, goal, task) = RunningTask();
        var store = new CancelDispatchIntentTestStore();
        if (rejected || olderProcess)
        {
            var intent = Intent(goal, task);
            if (olderProcess)
            {
                var payload = JsonSerializer.Deserialize<CancelDispatchOperatorIntentPayload>(intent.PayloadJson)!;
                intent = intent with { PayloadJson = JsonSerializer.Serialize(payload with { ProcessStartedAt = payload.ProcessStartedAt.AddTicks(-1) }) };
            }
            store.Add(intent with { Status = rejected ? OperatorIntentStatus.Rejected : OperatorIntentStatus.Pending });
        }
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false) { OperatorIntents = store };

        Xunit.Assert.Equal(1, runner.RequeueInterruptedDispatches(kernel));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("Auto-requeued orphaned", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ConductorCancelledRoundStillRequeuesWithoutIntent()
    {
        var (kernel, goal, task) = RunningTask();
        kernel.RecordTaskProcessCancelled(goal.Id, task.Id, task.LastProcess! with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = true,
            WasCancelledByConductor = true
        });
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false)
        {
            OperatorIntents = new CancelDispatchIntentTestStore()
        };

        Xunit.Assert.Equal(1, runner.RequeueInterruptedDispatches(kernel));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("after conductor loop stop", StringComparison.Ordinal));
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) RunningTask(
        DateTimeOffset? startedAt = null, AgentRole role = AgentRole.Developer)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("operator cancel race", [new TaskSpec(TaskId.New(), "developer", role)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var now = startedAt ?? DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "worker command", "C:\\goal", now));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4242, "worker command", "C:\\goal", "out.log", "err.log",
                Path.Combine(Path.GetTempPath(), $"missing-cancel-exit-{Guid.NewGuid():N}"), now, null, null));
        return (kernel, goal, task);
    }

    internal static OperatorIntentRecord Intent(Goal goal, TaskSpec task)
    {
        var process = task.LastProcess!;
        var payload = new CancelDispatchOperatorIntentPayload(process.ProcessId, process.StartedAt,
            BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, task.LastDispatch!));
        return new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.CancelDispatch, goal.Id.Value, task.Id.Value,
            JsonSerializer.Serialize(payload), [], "operator", "cli", "local-process", DateTimeOffset.UtcNow);
    }
}

internal sealed class CancelDispatchIntentTestStore : IOperatorIntentStore
{
    private readonly List<OperatorIntentRecord> _items = [];
    public bool ThrowOnEnqueue { get; set; }
    public Action? BeforeEnqueue { get; set; }

    public void Add(OperatorIntentRecord intent) => _items.Add(intent);

    public Task<OperatorIntentRecord> EnqueueAsync(OperatorIntentRecord intent, CancellationToken cancellationToken = default)
    {
        BeforeEnqueue?.Invoke();
        if (ThrowOnEnqueue) throw new IOException("intent append failed");
        _items.Add(intent);
        return Task.FromResult(intent);
    }

    public Task<OperatorIntentRecord?> ClaimNextAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default)
    {
        var index = _items.FindIndex(item => item.GoalId == goalId && item.Status == OperatorIntentStatus.Pending);
        if (index < 0) return Task.FromResult<OperatorIntentRecord?>(null);
        _items[index] = _items[index] with { Status = OperatorIntentStatus.Claimed, ClaimOwner = claimOwner };
        return Task.FromResult<OperatorIntentRecord?>(_items[index]);
    }

    public Task<OperatorIntentRecord?> ClaimNextByVerbAsync(string goalId, string verb, string claimOwner, CancellationToken cancellationToken = default) =>
        Task.FromResult<OperatorIntentRecord?>(null);

    public Task CompleteAsync(string intentId, string claimOwner, OperatorIntentStatus status, string outcome,
        DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        var index = _items.FindIndex(item => item.Id == intentId);
        _items[index] = _items[index] with { Status = status, Outcome = outcome, CompletedAt = completedAt };
        return Task.CompletedTask;
    }

    public Task<OperatorIntentRecord?> GetAsync(string intentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<OperatorIntentRecord?>(_items.FirstOrDefault(item => item.Id == intentId));

    public Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(string goalId, int limit = 20,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OperatorIntentRecord>>(_items.Where(item => item.GoalId == goalId).Take(limit).ToArray());

    public Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_items.Where(item => item.Status is OperatorIntentStatus.Pending or OperatorIntentStatus.Claimed)
            .Select(item => item.GoalId).Distinct().ToArray());

    public Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(
        IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>>(
            new Dictionary<string, ActionableOperatorIntentSummary>());

    public void AcknowledgeWake(string intentId) { }
}
