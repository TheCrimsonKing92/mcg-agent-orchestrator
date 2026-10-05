using System.Data.Common;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static void DrainGoalLifecycleEventOutbox(
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace)
    {
        if (repository is not IOrchestratorStateOutboxRepository outbox)
            return;

        DrainGoalEffectKind(outbox, GoalLifecycleEventOutbox.Kind, "lifecycle event",
            (message, token) => GoalLifecycleEventOutbox.Process(repository, workspace, message, token));
        DrainGoalEffectKind(outbox, GoalParkAttentionResolutionOutbox.Kind, "park attention resolution",
            (message, token) => GoalParkAttentionResolutionOutbox.Process(repository, workspace, message, token));
        DrainGoalEffectKind(outbox, GoalAbandonAfterCommitOutbox.Kind, "abandon cleanup",
            (message, token) => GoalAbandonAfterCommitOutbox.Process(repository, workspace, message, token));
    }

    private static void DrainGoalEffectKind(
        IOrchestratorStateOutboxRepository outbox, string kind, string label,
        Func<OrchestratorStateOutboxMessage, CancellationToken, Task<OrchestratorStateOutboxProcessingResult>> process)
    {
        IReadOnlyList<OrchestratorStateOutboxMessage> messages;
        try
        {
            messages = outbox.ListOutboxMessagesAsync(kind).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteGoalLifecycleDrainWarning(label, "could not list pending messages", ex);
            return;
        }

        foreach (var message in messages)
        {
            try
            {
                if (kind == GoalAbandonAfterCommitOutbox.Kind &&
                    GoalAbandonAfterCommitOutbox.IsWaitingForLifecycle(outbox, message).GetAwaiter().GetResult())
                    continue;
                OrchestratorStateOutboxProcessingResult? result = null;
                var claimed = DeliverGoalOutboxMessage(outbox, message.Id, async (pending, token) =>
                {
                    result = await process(pending, token);
                    return result;
                });
                if (claimed && result?.Disposition == OrchestratorStateOutboxDisposition.Quarantine)
                    Console.Error.WriteLine(
                        $"Warning: goal {label} outbox message '{message.Id}' was quarantined: {result.Detail}");
                else if (claimed && result?.Detail is not null)
                    Console.Error.WriteLine($"Warning: goal {label} outbox message '{message.Id}' skipped: {result.Detail}");
            }
            catch (Exception ex)
            {
                WriteGoalLifecycleDrainWarning(label, $"message '{message.Id}' remains pending", ex);
            }
        }
    }

    private static void WriteGoalLifecycleDrainWarning(string label, string detail, Exception ex) =>
        Console.Error.WriteLine(
            $"Warning: goal {label} drain {detail}; unrelated command will continue. {ex.GetType().Name}: {ex.Message}");

    private static bool DeliverGoalOutboxMessage(IOrchestratorStateOutboxRepository outbox, string messageId,
        Func<OrchestratorStateOutboxMessage, CancellationToken, Task<OrchestratorStateOutboxProcessingResult>> process) =>
        outbox.TryProcessOutboxMessageAsync(messageId, process).GetAwaiter().GetResult();

    private static int DeliverCommittedGoalParkAttentionResolution(
        IOrchestratorStateOutboxRepository outbox, OrchestratorWorkspace workspace, GoalId goalId, string messageId)
    {
        var count = 0;
        DeliverCommittedGoalEffect(goalId, GoalStatus.Parked, "attention resolution", () =>
            DeliverGoalOutboxMessage(outbox, messageId, (message, token) =>
                GoalParkAttentionResolutionOutbox.Process(outbox, workspace, message, token, value => count = value)));
        return count;
    }

    private static GoalAbandonPlan? DeliverCommittedGoalAbandonCleanup(
        IOrchestratorStateOutboxRepository outbox, OrchestratorWorkspace workspace, GoalId goalId,
        GoalStatus status, string messageId, AgentOrchestratorKernel kernel, GoalWorktreeCleanupHooks hooks)
    {
        GoalAbandonPlan? plan = null;
        DeliverCommittedGoalEffect(goalId, status, "abandon cleanup", () =>
            DeliverGoalOutboxMessage(outbox, messageId, (message, token) =>
                GoalAbandonAfterCommitOutbox.Process(outbox, workspace, message, token, kernel, hooks, value => plan = value)));
        return plan;
    }

    private static void DeliverCommittedGoalEffect(GoalId goalId, GoalStatus status, string effect, Action deliver)
    {
        try { deliver(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException)
        {
            throw new CliCommandHandlers.GoalLifecycleProjectionException(
                $"Goal '{goalId.Value[..8]}' is committed {status}, but {effect} delivery failed: {ex.Message.TrimEnd().TrimEnd('.')}. " +
                "The delivery is pending and will be retried by the next writer-path command.", ex);
        }
    }

    private static void DeliverCommittedGoalCancelEvent(
        IOrchestratorStateOutboxRepository outbox,
        OrchestratorWorkspace workspace,
        GoalId goalId,
        string messageId) =>
        DeliverCommittedGoalLifecycleEvent(outbox, workspace, goalId, GoalStatus.Cancelled, messageId);

    private static void DeliverCommittedGoalLifecycleEvent(
        IOrchestratorStateOutboxRepository outbox,
        OrchestratorWorkspace workspace,
        GoalId goalId,
        GoalStatus committedStatus,
        string messageId)
    {
        try
        {
            DeliverGoalOutboxMessage(outbox, messageId,
                (message, token) => GoalLifecycleEventOutbox.Process(outbox, workspace, message, token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliCommandHandlers.GoalLifecycleProjectionException(
                $"Goal '{goalId.Value[..8]}' is committed {committedStatus}, but lifecycle event projection failed: {ex.Message} " +
                "The lifecycle event delivery is pending and will be retried by the next writer-path command.", ex);
        }
    }
}
