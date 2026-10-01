namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OperatorEscalationAction(
    string Label,
    string Command,
    bool RequiresConfirm = false,
    bool RequiresInput = false,
    long? ExpectedGoalStateVersion = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record OperatorEscalation(
    string InboxItemId,
    string GoalId,
    string GoalPrefix,
    string Kind,
    string Title,
    string Summary,
    string KeyEvidence,
    IReadOnlyList<OperatorEscalationAction> Actions);

public sealed record OperatorDecision(
    string InboxItemId,
    int ActionIndex,
    string? FreeText,
    string ActorId,
    string IdempotencyKey);

public interface IOperatorChannel
{
    string ChannelType { get; }
    Task SendEscalationAsync(OperatorEscalation escalation, CancellationToken cancellationToken = default);
}
