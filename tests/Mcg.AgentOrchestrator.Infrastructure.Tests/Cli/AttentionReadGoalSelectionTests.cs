using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Pure value selection; no shared state or I/O.
public sealed class AttentionReadGoalSelectionTests
{
    [Theory]
    [InlineData("pArKeD", true)]
    [InlineData("pArKeD", false)]
    [InlineData("cOmPlEtEd", true)]
    [InlineData("cOmPlEtEd", false)]
    [InlineData("fAiLeD", true)]
    [InlineData("fAiLeD", false)]
    [InlineData("cAnCeLlEd", true)]
    [InlineData("cAnCeLlEd", false)]
    [InlineData("sUpErSeDeD", true)]
    [InlineData("sUpErSeDeD", false)]
    [InlineData("aCtIvE", true)]
    [InlineData("aCtIvE", false)]
    public void References_SelectOnlyLiveMetadata(string status, bool withWait)
    {
        var item = Item("goal");
        var result = AttentionReadGoalSelection.Select([Metadata("goal", status)],
            withWait ? [Wait("GOAL")] : [], withWait ? [] : [item], withWait ? [] : [item]);

        Assert.Equal(status.Equals("Parked", StringComparison.OrdinalIgnoreCase), result.Decline);
        Assert.Equal(status.Equals("Active", StringComparison.OrdinalIgnoreCase) ? ["goal"] : Array.Empty<string>(),
            result.GoalIds.Select(id => id.Value));
    }

    [Theory]
    [InlineData(CollaborationItemType.Notice)]
    [InlineData(CollaborationItemType.Capture)]
    [InlineData(CollaborationItemType.Execute)]
    [InlineData(CollaborationItemType.Ideate)]
    public void NonReachUpItems_DoNotSelectOrDecline(CollaborationItemType type)
    {
        CollaborationItem[] items = [Item("active", type), Item("parked", type)];
        var result = AttentionReadGoalSelection.Select(
            [Metadata("active"), Metadata("parked", "Parked")], [], items, items);

        Assert.False(result.Decline);
        Assert.Empty(result.GoalIds);
    }

    [Fact]
    public void MissingAndCrossGoalReferences_DoNotSelect()
    {
        CollaborationItem[] items = [Item(null), Item("missing")];
        var result = AttentionReadGoalSelection.Select([Metadata("active")], [Wait("missing")], items, items);

        Assert.False(result.Decline);
        Assert.Empty(result.GoalIds);
    }

    [Fact]
    public void DuplicateReferences_KeepMetadataSpellingAndOrdinalOrder()
    {
        var result = AttentionReadGoalSelection.Select(
            [Metadata("aa"), Metadata("BB"), Metadata("Ab")],
            [Wait("AA"), Wait("bb"), Wait("ab")], [],
            [Item("Aa"), Item("Bb"), Item("AB")]);

        Assert.False(result.Decline);
        Assert.Equal(["Ab", "BB", "aa"], result.GoalIds.Select(id => id.Value));
    }

    [Theory]
    [InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Raised, true)]
    [InlineData(CollaborationItemType.Clarification, CollaborationItemStatus.Raised, true)]
    [InlineData(CollaborationItemType.Verify, CollaborationItemStatus.Raised, true)]
    [InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Delivered, true)]
    [InlineData(CollaborationItemType.Clarification, CollaborationItemStatus.Delivered, true)]
    [InlineData(CollaborationItemType.Verify, CollaborationItemStatus.Delivered, true)]
    [InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Resolved, false)]
    [InlineData(CollaborationItemType.Clarification, CollaborationItemStatus.Resolved, false)]
    [InlineData(CollaborationItemType.Verify, CollaborationItemStatus.Resolved, false)]
    [InlineData(CollaborationItemType.Decision, CollaborationItemStatus.Closed, false)]
    [InlineData(CollaborationItemType.Clarification, CollaborationItemStatus.Closed, false)]
    [InlineData(CollaborationItemType.Verify, CollaborationItemStatus.Closed, false)]
    public void ParkedStoredItems_DeclineOnlyForOpenReachUpRows(
        CollaborationItemType type, CollaborationItemStatus status, bool decline)
    {
        var result = AttentionReadGoalSelection.Select(
            [Metadata("parked", "pArKeD"), Metadata("active")], [Wait("active")],
            [Item("PARKED", type, status)], []);

        Assert.Equal(decline, result.Decline);
        Assert.Equal(decline ? Array.Empty<string>() : ["active"], result.GoalIds.Select(id => id.Value));
    }

    [Fact]
    public void ParkedOpenWait_DeclinesAndDiscardsLiveReferences()
    {
        var result = AttentionReadGoalSelection.Select(
            [Metadata("parked", "pArKeD"), Metadata("active")],
            [Wait("PARKED"), Wait("active")], [], [Item("active")]);

        Assert.True(result.Decline);
        Assert.Empty(result.GoalIds);
    }

    private static GoalSummary Metadata(string id, string status = "Active") => new(id, status, "Goal", "");

    private static HumanInputRequestSnapshot Wait(string goalId) =>
        new("wait", goalId, null, "Question", DateTimeOffset.MinValue);

    private static CollaborationItem Item(string? goalId,
        CollaborationItemType type = CollaborationItemType.Decision,
        CollaborationItemStatus status = CollaborationItemStatus.Raised) =>
        new("item", type, goalId, status, "Subject", "Body", null, DateTimeOffset.MinValue, null, null);
}
