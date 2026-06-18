using Mcg.AgentOrchestrator.Core;

public sealed class CollaborationItemTests
{
    // --- Lifecycle transition table ---

    [Xunit.Theory(DisplayName = "Lifecycle_valid_transitions_are_accepted")]
    [Xunit.InlineData(CollaborationItemStatus.Raised, CollaborationItemStatus.Delivered)]
    [Xunit.InlineData(CollaborationItemStatus.Raised, CollaborationItemStatus.Resolved)]
    [Xunit.InlineData(CollaborationItemStatus.Raised, CollaborationItemStatus.Closed)]
    [Xunit.InlineData(CollaborationItemStatus.Delivered, CollaborationItemStatus.Resolved)]
    [Xunit.InlineData(CollaborationItemStatus.Delivered, CollaborationItemStatus.Closed)]
    [Xunit.InlineData(CollaborationItemStatus.Resolved, CollaborationItemStatus.Closed)]
    public void ValidTransitionsAreAccepted(CollaborationItemStatus from, CollaborationItemStatus to)
    {
        Assert.True(CollaborationItemLifecycle.CanTransition(from, to));
    }

    [Xunit.Theory(DisplayName = "Lifecycle_invalid_transitions_are_rejected")]
    [Xunit.InlineData(CollaborationItemStatus.Delivered, CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemStatus.Resolved, CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemStatus.Resolved, CollaborationItemStatus.Delivered)]
    [Xunit.InlineData(CollaborationItemStatus.Closed, CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemStatus.Closed, CollaborationItemStatus.Delivered)]
    [Xunit.InlineData(CollaborationItemStatus.Closed, CollaborationItemStatus.Resolved)]
    [Xunit.InlineData(CollaborationItemStatus.Raised, CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemStatus.Closed, CollaborationItemStatus.Closed)]
    public void InvalidTransitionsAreRejected(CollaborationItemStatus from, CollaborationItemStatus to)
    {
        Assert.False(CollaborationItemLifecycle.CanTransition(from, to));
    }

    // --- Terminal state ---

    [Xunit.Theory(DisplayName = "Lifecycle_terminal_states_are_Resolved_and_Closed")]
    [Xunit.InlineData(CollaborationItemStatus.Resolved)]
    [Xunit.InlineData(CollaborationItemStatus.Closed)]
    public void TerminalStatesAreResolvedAndClosed(CollaborationItemStatus status)
    {
        Assert.True(CollaborationItemLifecycle.IsTerminal(status));
    }

    [Xunit.Theory(DisplayName = "Lifecycle_non_terminal_states_are_Raised_and_Delivered")]
    [Xunit.InlineData(CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemStatus.Delivered)]
    public void NonTerminalStatesAreRaisedAndDelivered(CollaborationItemStatus status)
    {
        Assert.False(CollaborationItemLifecycle.IsTerminal(status));
    }

    // --- Attention queue membership ---

    [Xunit.Theory(DisplayName = "AttentionQueue_includes_Decision_Clarification_Verify_in_non_terminal_status")]
    [Xunit.InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Delivered)]
    [Xunit.InlineData(CollaborationItemType.Clarification, CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemType.Clarification, CollaborationItemStatus.Delivered)]
    [Xunit.InlineData(CollaborationItemType.Verify, CollaborationItemStatus.Raised)]
    [Xunit.InlineData(CollaborationItemType.Verify, CollaborationItemStatus.Delivered)]
    public void AttentionQueueIncludesReachUpItemsInNonTerminalStatus(
        CollaborationItemType type, CollaborationItemStatus status)
    {
        var items = new[] { MakeItem(type, status) };
        var queue = CollaborationItemLifecycle.BuildAttentionQueue(items);
        Assert.True(queue.Count == 1);
    }

    [Xunit.Theory(DisplayName = "AttentionQueue_excludes_resolved_and_closed_reach_up_items")]
    [Xunit.InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Resolved)]
    [Xunit.InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Closed)]
    [Xunit.InlineData(CollaborationItemType.Clarification, CollaborationItemStatus.Resolved)]
    [Xunit.InlineData(CollaborationItemType.Verify, CollaborationItemStatus.Closed)]
    public void AttentionQueueExcludesTerminalItems(CollaborationItemType type, CollaborationItemStatus status)
    {
        var items = new[] { MakeItem(type, status) };
        var queue = CollaborationItemLifecycle.BuildAttentionQueue(items);
        Assert.True(queue.Count == 0);
    }

    [Xunit.Theory(DisplayName = "AttentionQueue_excludes_Notice_Capture_Execute_Ideate_types")]
    [Xunit.InlineData(CollaborationItemType.Notice)]
    [Xunit.InlineData(CollaborationItemType.Capture)]
    [Xunit.InlineData(CollaborationItemType.Execute)]
    [Xunit.InlineData(CollaborationItemType.Ideate)]
    public void AttentionQueueExcludesIntakeTypes(CollaborationItemType type)
    {
        var items = new[] { MakeItem(type, CollaborationItemStatus.Raised) };
        var queue = CollaborationItemLifecycle.BuildAttentionQueue(items);
        Assert.True(queue.Count == 0);
    }

    // --- Attention queue ordering ---

    [Xunit.Fact(DisplayName = "AttentionQueue_orders_Decision_before_Clarification_before_Verify")]
    public void AttentionQueueOrdersByTypePriority()
    {
        var baseTime = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            MakeItem(CollaborationItemType.Verify, CollaborationItemStatus.Raised, baseTime),
            MakeItem(CollaborationItemType.Clarification, CollaborationItemStatus.Raised, baseTime),
            MakeItem(CollaborationItemType.Decision, CollaborationItemStatus.Raised, baseTime),
        };

        var queue = CollaborationItemLifecycle.BuildAttentionQueue(items);

        Assert.True(queue.Count == 3);
        Assert.Equal(CollaborationItemType.Decision, queue[0].Type);
        Assert.Equal(CollaborationItemType.Clarification, queue[1].Type);
        Assert.Equal(CollaborationItemType.Verify, queue[2].Type);
    }

    [Xunit.Fact(DisplayName = "AttentionQueue_orders_same_type_by_raisedAt_ascending")]
    public void AttentionQueueOrdersSameTypeOldestFirst()
    {
        var baseTime = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            MakeItem(CollaborationItemType.Decision, CollaborationItemStatus.Raised, baseTime.AddHours(2), "third"),
            MakeItem(CollaborationItemType.Decision, CollaborationItemStatus.Raised, baseTime, "first"),
            MakeItem(CollaborationItemType.Decision, CollaborationItemStatus.Raised, baseTime.AddHours(1), "second"),
        };

        var queue = CollaborationItemLifecycle.BuildAttentionQueue(items);

        Assert.True(queue.Count == 3);
        Assert.Equal("first", queue[0].Subject);
        Assert.Equal("second", queue[1].Subject);
        Assert.Equal("third", queue[2].Subject);
    }

    [Xunit.Fact(DisplayName = "AttentionQueue_type_priority_dominates_raisedAt")]
    public void AttentionQueueTypePriorityDominatesRaisedAt()
    {
        var baseTime = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        // Verify was raised an hour before Decision, but Decision wins on type priority.
        var items = new[]
        {
            MakeItem(CollaborationItemType.Verify, CollaborationItemStatus.Raised, baseTime, "old-verify"),
            MakeItem(CollaborationItemType.Decision, CollaborationItemStatus.Raised, baseTime.AddHours(1), "new-decision"),
        };

        var queue = CollaborationItemLifecycle.BuildAttentionQueue(items);

        Assert.True(queue.Count == 2);
        Assert.Equal(CollaborationItemType.Decision, queue[0].Type);
        Assert.Equal(CollaborationItemType.Verify, queue[1].Type);
    }

    [Xunit.Fact(DisplayName = "AttentionQueue_empty_when_no_qualifying_items")]
    public void AttentionQueueEmptyWhenNoQualifyingItems()
    {
        var items = new[]
        {
            MakeItem(CollaborationItemType.Notice, CollaborationItemStatus.Raised),
            MakeItem(CollaborationItemType.Decision, CollaborationItemStatus.Resolved),
            MakeItem(CollaborationItemType.Capture, CollaborationItemStatus.Raised),
        };

        var queue = CollaborationItemLifecycle.BuildAttentionQueue(items);

        Assert.True(queue.Count == 0);
    }

    private static CollaborationItem MakeItem(
        CollaborationItemType type,
        CollaborationItemStatus status,
        DateTimeOffset? raisedAt = null,
        string subject = "test subject") =>
        new(
            Guid.NewGuid().ToString("n"),
            type,
            null,
            status,
            subject,
            "test body",
            null,
            raisedAt ?? DateTimeOffset.UtcNow,
            null,
            null);
}
