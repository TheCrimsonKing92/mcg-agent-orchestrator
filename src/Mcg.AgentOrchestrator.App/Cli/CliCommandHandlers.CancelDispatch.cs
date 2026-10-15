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
            "cancel-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--completion-build-operation <operation-id>]");
        var goal = context.CurrentGoal!;
        var selector = GetFlagValue(parts, "--completion-build-operation");
        if (parts.Contains("--completion-build-operation") && string.IsNullOrWhiteSpace(selector))
            throw new ArgumentException("--completion-build-operation requires the exact attached operation id.");
        var operation = selector is null ? null : task.CompletionBuildEvidence;
        if (selector is not null && (operation is null || !string.Equals(selector, operation.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException("Completion build operation selector mismatch; use the exact attached operation id.");
        var process = operation?.WorkerProcess ?? task.LastProcess ??
            throw new InvalidOperationException($"Task '{task.Id}' has no background process to cancel.");
        var dispatch = operation is not null ? null : task.LastDispatch ??
            throw new InvalidOperationException($"Task '{task.Id}' has no dispatch for cancellation.");
        if (operation is null && task.Status != WorkTaskStatus.Running &&
            !(task.Status == WorkTaskStatus.Cancelled && task.WasCancelledByConductor))
        {
            throw new InvalidOperationException($"Task '{task.Id}' is {task.Status}; cancel-dispatch requires a running dispatch.");
        }

        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(
            parts, CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
        var intentId = Guid.NewGuid().ToString("N");
        var payload = new CancelDispatchOperatorIntentPayload(
            process.ProcessId, process.StartedAt,
            operation?.DispatchId ?? BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, dispatch!),
            operation is null ? null : CompletionBuildEvidenceTarget.From(operation));
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

        if (operation is null) (stopProcessTree ?? new BackgroundDispatchRunner().StopProcessTree)(process);
        OperatorIntentSubmissionOutput.PrintCancelDispatch(persisted, context.Workspace.OrchestratorDirectory,
            goal.Id.Value, task.Id.Value,
            operation is not null ? "Conductor will reconcile the exact completion build operation and release proven custody." :
            $"Next step once applied: {OperatorIntentCoordinator.BuildCancelDispatchRecoveryCommand(goal, task)} " +
            "(close and reopen-regate do not apply to a cancelled task).", emitAgentReadEnvelope);
        return false;
    }
}
