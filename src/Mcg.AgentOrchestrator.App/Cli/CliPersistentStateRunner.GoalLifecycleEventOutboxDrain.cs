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

        IReadOnlyList<OrchestratorStateOutboxMessage> messages;
        try
        {
            messages = outbox.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteGoalLifecycleDrainWarning("could not list pending messages", ex);
            return;
        }

        foreach (var message in messages)
        {
            try
            {
                OrchestratorStateOutboxProcessingResult? result = null;
                var claimed = outbox.TryProcessOutboxMessageAsync(message.Id, async (pending, token) =>
                {
                    result = await GoalLifecycleEventOutbox.Process(repository, workspace, pending, token);
                    return result;
                }).GetAwaiter().GetResult();
                if (claimed && result?.Disposition == OrchestratorStateOutboxDisposition.Quarantine)
                    Console.Error.WriteLine(
                        $"Warning: goal lifecycle event outbox message '{message.Id}' was quarantined: {result.Detail}");
            }
            catch (Exception ex)
            {
                WriteGoalLifecycleDrainWarning($"message '{message.Id}' remains pending", ex);
            }
        }
    }

    private static void WriteGoalLifecycleDrainWarning(string detail, Exception ex) =>
        Console.Error.WriteLine(
            $"Warning: goal lifecycle event drain {detail}; unrelated command will continue. {ex.GetType().Name}: {ex.Message}");

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
            outbox.TryProcessOutboxMessageAsync(messageId,
                (message, token) => GoalLifecycleEventOutbox.Process(outbox, workspace, message, token))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliCommandHandlers.GoalLifecycleProjectionException(
                $"Goal '{goalId.Value[..8]}' is committed {committedStatus}, but lifecycle event projection failed: {ex.Message} " +
                "The lifecycle event delivery is pending and will be retried by the next writer-path command.", ex);
        }
    }
}
