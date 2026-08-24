namespace Mcg.AgentOrchestrator.Core;

public interface ICollaborationItemStore
{
    Task<CollaborationItem> RaiseAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string? correlationKey = null,
        CancellationToken cancellationToken = default);

    Task<CollaborationItem> RaiseWithActionsAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string correlationKey,
        IReadOnlyList<CollaborationActionBinding> actions,
        CancellationToken cancellationToken = default);

    Task<bool> TryResolveAsync(
        string correlationKey,
        string resolution,
        CancellationToken cancellationToken = default,
        int? briefVersion = null);

    Task<int> ResolveOpenForGoalAsync(
        string goalId,
        string resolution,
        CancellationToken cancellationToken = default);

    Task<bool> TryMarkDeliveredAsync(
        string correlationKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationItem>> GetAttentionQueueAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationItem>> ListAsync(
        string? goalId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationItem>> ListForGoalIdsAsync(
        IEnumerable<string> goalIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationBoundAction>> ListActionsAsync(
        string correlationKey,
        CancellationToken cancellationToken = default);

    Task UpdateRenderedContentHashAsync(
        IEnumerable<string> correlationKeys,
        string renderedContentHash,
        CancellationToken cancellationToken = default);

    Task<CollaborationActionApplyResult> TryClaimActionAsync(
        string correlationKey,
        int actionIndex,
        string actorId,
        string interactionId,
        long? currentGoalStateVersion,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default);

    Task<CollaborationDecisionAuditEntry> RecordRejectedDecisionAsync(
        string correlationKey,
        int? actionIndex,
        string actorId,
        string interactionId,
        string reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationDecisionAuditEntry>> ListDecisionAuditAsync(
        CancellationToken cancellationToken = default);

    Task<DecisionRequest> RaiseDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<DecisionState?> GetDecisionStateAsync(
        string requestId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DecisionRequest>> ListDecisionRequestsAsync(
        string? goalId = null,
        CancellationToken cancellationToken = default);

    Task<NotificationDelivery> RecordNotificationDeliveryAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDelivery>> ListNotificationDeliveriesAsync(
        string requestId,
        CancellationToken cancellationToken = default);

    Task<DecisionReceipt> RecordDecisionAsync(
        string requestId,
        string actorId,
        string channel,
        AuthorizationTier authenticationAssurance,
        long? expectedGoalStateVersion,
        DecisionResponse response,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default);

    Task<DecisionReceipt> RecordExpiredDefaultDispositionAsync(
        string requestId,
        DateTimeOffset expiredAt,
        CancellationToken cancellationToken = default);

    Task<DecisionEffectApplyResult> TryApplyDecisionEffectAsync(
        string requestId,
        string decisionReceiptId,
        DecisionActionRef actionRef,
        long? currentGoalStateVersion,
        string result,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken = default);
}

public sealed record CollaborationActionBinding(
    string Label,
    string Command,
    bool RequiresConfirmation = false,
    bool RequiresInput = false,
    long? ExpectedGoalStateVersion = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record CollaborationBoundAction(
    string CorrelationKey,
    int ActionIndex,
    string Label,
    string Command,
    bool RequiresConfirmation,
    bool RequiresInput,
    long? ExpectedGoalStateVersion,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt,
    string? RenderedContentHash);

public sealed record CollaborationActionApplyResult(
    bool Applied,
    bool Duplicate,
    CollaborationBoundAction? Action,
    CollaborationDecisionAuditEntry Audit,
    string? ErrorMessage);

public sealed record CollaborationDecisionAuditEntry(
    long Id,
    string CorrelationKey,
    int? ActionIndex,
    string ActorId,
    string InteractionId,
    string Outcome,
    string? Command,
    string? RejectionReason,
    long? ExpectedGoalStateVersion,
    long? ActualGoalStateVersion,
    string? RenderedContentHash,
    DateTimeOffset DecidedAt);

public sealed record DecisionEffectApplyResult(
    bool Applied,
    bool Duplicate,
    EffectReceipt Receipt,
    string? ErrorMessage);
