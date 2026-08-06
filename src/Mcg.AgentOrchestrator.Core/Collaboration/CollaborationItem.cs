namespace Mcg.AgentOrchestrator.Core;

public enum CollaborationItemType
{
    Decision,
    Clarification,
    Verify,
    Notice,
    Capture,
    Execute,
    Ideate
}

public enum CollaborationItemStatus
{
    Raised,
    Delivered,
    Resolved,
    Closed
}

public sealed record CollaborationItem(
    string Id,
    CollaborationItemType Type,
    string? GoalId,
    CollaborationItemStatus Status,
    string Subject,
    string Body,
    string? CorrelationKey,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution,
    IReadOnlyList<HumanInputAnswerRecord>? AnswerHistory = null)
{
    public HumanInputAnswerRecord? AuthoritativeAnswer =>
        AnswerHistory?.LastOrDefault(answer => !answer.IsRetracted);
}

public static class CollaborationItemLifecycle
{
    private static readonly CollaborationItemStatus[] TerminalStatuses =
        [CollaborationItemStatus.Resolved, CollaborationItemStatus.Closed];

    // Reach-up types that appear in the attention queue.
    private static readonly CollaborationItemType[] ReachUpTypes =
        [CollaborationItemType.Decision, CollaborationItemType.Clarification, CollaborationItemType.Verify];

    public static bool CanTransition(CollaborationItemStatus from, CollaborationItemStatus to) =>
        (from, to) switch
        {
            (CollaborationItemStatus.Raised, CollaborationItemStatus.Delivered) => true,
            (CollaborationItemStatus.Raised, CollaborationItemStatus.Resolved) => true,
            (CollaborationItemStatus.Raised, CollaborationItemStatus.Closed) => true,
            (CollaborationItemStatus.Delivered, CollaborationItemStatus.Resolved) => true,
            (CollaborationItemStatus.Delivered, CollaborationItemStatus.Closed) => true,
            (CollaborationItemStatus.Resolved, CollaborationItemStatus.Closed) => true,
            _ => false
        };

    public static bool IsTerminal(CollaborationItemStatus status) =>
        Array.IndexOf(TerminalStatuses, status) >= 0;

    public static bool IsReachUpType(CollaborationItemType type) =>
        Array.IndexOf(ReachUpTypes, type) >= 0;

    // Lower value = higher priority in attention queue. Decision first.
    public static int AttentionPriority(CollaborationItemType type) => type switch
    {
        CollaborationItemType.Decision => 0,
        CollaborationItemType.Clarification => 1,
        CollaborationItemType.Verify => 2,
        _ => int.MaxValue
    };

    // Filters and orders items for the attention queue:
    // reach-up types (Decision/Clarification/Verify) in non-terminal status,
    // ordered by type priority then raisedAt ascending.
    public static IReadOnlyList<CollaborationItem> BuildAttentionQueue(
        IEnumerable<CollaborationItem> items) =>
        items
            .Where(i => IsReachUpType(i.Type) && !IsTerminal(i.Status))
            .OrderBy(i => AttentionPriority(i.Type))
            .ThenBy(i => i.RaisedAt)
            .ToList();
}
