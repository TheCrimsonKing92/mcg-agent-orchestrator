using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCancelDispatchIntentOrderingTests
{
    [Xunit.Fact]
    public async Task CommandAppendsIntentBeforeStoppingWithoutWritingCancelledState()
    {
        var (context, goal, task) = CreateContext();
        var calls = new List<string>();
        var store = new CancelDispatchIntentTestStore { BeforeEnqueue = () => calls.Add("append") };

        var shouldSave = CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1"], context, store, _ => calls.Add("stop"));

        Xunit.Assert.Equal(["append", "stop"], calls);
        Xunit.Assert.False(shouldSave);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.False(task.LastProcess!.WasCancelled);
        var intent = Xunit.Assert.Single(await store.ListForGoalAsync(goal.Id.Value));
        Xunit.Assert.Equal(OperatorIntentVerbs.CancelDispatch, intent.Verb);
        Xunit.Assert.Equal(task.Id.Value, intent.TaskId);
        var payload = JsonSerializer.Deserialize<CancelDispatchOperatorIntentPayload>(
            intent.PayloadJson, OperatorIntentJson.Options)!;
        Xunit.Assert.Equal(task.LastProcess.ProcessId, payload.ProcessId);
        Xunit.Assert.Equal(task.LastProcess.StartedAt, payload.ProcessStartedAt);
        Xunit.Assert.Equal(BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, task.LastDispatch!), payload.DispatchId);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskCancelled);
    }

    [Xunit.Fact]
    public void FailedAppendLeavesWorkerRunningAndStateUntouched()
    {
        var (context, _, task) = CreateContext();
        var stopped = false;
        var store = new CancelDispatchIntentTestStore { ThrowOnEnqueue = true };

        var error = Xunit.Assert.Throws<IOException>(() => CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1"], context, store, _ => stopped = true));

        Xunit.Assert.Contains("intent append failed", error.Message, StringComparison.Ordinal);
        Xunit.Assert.False(stopped);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.False(task.LastProcess!.WasCancelled);
    }

    [Xunit.Theory]
    [Xunit.InlineData("refresh-dispatch")]
    [Xunit.InlineData("refresh-dispatches")]
    public async Task PendingCancelIntentBlocksDirectCliRefresh(string command)
    {
        var (context, goal, task) = CreateContext(DateTimeOffset.UtcNow.AddHours(-2));
        var store = SqliteOperatorIntentStore.ForDirectories(
            context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory);
        await store.EnqueueAsync(BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(goal, task));
        File.WriteAllText(task.LastProcess!.ExitCodePath, "1");

        try
        {
            CliCommandHandlers.Execute(command == "refresh-dispatch"
                ? [command, "1"] : [command], context);

            Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
            Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        }
        finally
        {
            File.Delete(task.LastProcess.ExitCodePath);
        }
    }

    private static (CliExecutionContext Context, Goal Goal, TaskSpec Task) CreateContext(DateTimeOffset? startedAt = null)
    {
        var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask(startedAt);
        var workspace = OrchestratorWorkspace.ForDirectory(
            Path.Combine(Path.GetTempPath(), $"cancel-dispatch-test-{Guid.NewGuid():N}"));
        var context = new CliExecutionContext(kernel, workspace,
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), goal);
        return (context, goal, task);
    }
}
