using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal static bool ExecuteCancelDispatch(
        IReadOnlyList<string> parts,
        CliExecutionContext context,
        IOperatorIntentStore? intentStore = null,
        Action<TaskProcessRecord>? stopProcessTree = null)
    {
        parts = OperatorIntentSubmissionOutput.StripJsonFlag(parts, out var emitAgentReadEnvelope);
        var task = ResolveDispatchCommandTask(parts, context,
            "cancel-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
        var goal = context.CurrentGoal!;
        var process = task.LastProcess ??
            throw new InvalidOperationException($"Task '{task.Id}' has no background process to cancel.");
        var dispatch = task.LastDispatch ??
            throw new InvalidOperationException($"Task '{task.Id}' has no dispatch for cancellation.");
        if (task.Status != WorkTaskStatus.Running &&
            !(task.Status == WorkTaskStatus.Cancelled && task.WasCancelledByConductor))
        {
            throw new InvalidOperationException($"Task '{task.Id}' is {task.Status}; cancel-dispatch requires a running dispatch.");
        }

        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(
            parts, CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
        var intentId = Guid.NewGuid().ToString("N");
        var payload = new CancelDispatchOperatorIntentPayload(
            process.ProcessId, process.StartedAt,
            BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, dispatch));
        var intent = new OperatorIntentRecord(
            intentId,
            GetFlagValue(parts, "--idempotency-key") ?? intentId,
            OperatorIntentVerbs.CancelDispatch,
            goal.Id.Value,
            task.Id.Value,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options),
            [],
            attribution.Actor,
            attribution.Channel,
            attribution.AuthenticationAssurance,
            DateTimeOffset.UtcNow,
            ActorKind: attribution.ActorKind);
        var persisted = (intentStore ?? SqliteOperatorIntentStore.ForDirectories(
                context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory))
            .EnqueueAsync(intent).GetAwaiter().GetResult();

        (stopProcessTree ?? new BackgroundDispatchRunner().StopProcessTree)(process);
        OperatorIntentSubmissionOutput.PrintCancelDispatch(persisted, context.Workspace.OrchestratorDirectory,
            goal.Id.Value, task.Id.Value,
            $"Next step once applied: {OperatorIntentCoordinator.BuildCancelDispatchRecoveryCommand(goal, task)} " +
            "(close and reopen-regate do not apply to a cancelled task).", emitAgentReadEnvelope);
        return false;
    }
}
