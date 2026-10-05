using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCancelDispatchRecoveryHintTests
{
    [Xunit.Fact]
    public async Task Queued_cancel_keeps_original_line_then_names_recovery_command()
    {
        var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask();
        var root = Path.Combine(Path.GetTempPath(), $"cancel-cli-recovery-{Guid.NewGuid():N}");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var context = new CliExecutionContext(kernel, workspace,
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), goal);
        var store = new CancelDispatchIntentTestStore();

        var stdout = CaptureConsole(() => Assert.False(CliCommandHandlers.ExecuteCancelDispatch(
            ["cancel-dispatch", "1"], context, store, _ => { })));

        var intent = Assert.Single(await store.ListForGoalAsync(goal.Id.Value));
        var lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Operator intent queued: id={intent.Id} verb={intent.Verb} " +
            $"goal={goal.Id.Value} task={task.Id.Value} status={intent.Status}; " +
            $"poll with operator-intent-status {intent.Id} (or add --wait).", lines[0]);
        Assert.Equal($"Next step once applied: adjudicate --goal {goal.Id.Value[..8]} 1 route --cause <cause> --text-file <note> --evidence <reference> " +
            "(close and reopen-regate do not apply to a cancelled task).", lines[1]);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
    }
}
