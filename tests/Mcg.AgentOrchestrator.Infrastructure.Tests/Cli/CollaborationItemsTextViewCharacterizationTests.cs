using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and local fixtures.
// Expected text follows ConsoleViews.Attention.cs at base 781c2fd21.
public sealed class CollaborationItemsTextViewCharacterizationTests
{
    [Fact]
    public void PrintAttentionQueue_EmptyQueue_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            CollaborationItemsTextView.PrintAttentionQueue([]));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Attention queue: 0 open reach-up item(s)", "  none", "", ""), actual);
    }

    [Fact]
    public void PrintAttentionQueue_OpenItem_PreservesExactText()
    {
        var raisedAt = new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero);
        var item = new CollaborationItem(
            "aaaaaaaa00000000", CollaborationItemType.Decision, "short",
            CollaborationItemStatus.Raised, "Choose scope", " ", null, raisedAt, null, null,
            [new("answer-1", "Hidden history", raisedAt)]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            CollaborationItemsTextView.PrintAttentionQueue([item]));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Attention queue: 1 open reach-up item(s)",
            "  [Decision] aaaaaaaa status=Raised goal=short",
            "    dismiss: attention dismiss --item aaaaaaaa00000000",
            "    subject: Choose scope",
            "    raised: 2026-10-30 05:00:00Z",
            "", ""), actual);
    }

    [Theory]
    [InlineData(false, "Attention queue: 0 open reach-up item(s)")]
    [InlineData(true, "Attention queue: 0 reach-up item(s)")]
    public void Print_EmptyItems_PreservesExactText(bool includeHistory, string heading)
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            CollaborationItemsTextView.Print([], includeHistory));

        Assert.Equal(string.Join(Environment.NewLine, "", heading, "  none", "", ""), actual);
    }

    [Fact]
    public void Print_HistoryAndCustomIds_PreservesExactText()
    {
        var raisedAt = new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero);
        var resolved = new CollaborationItem(
            "bbbbbbbb00000000", CollaborationItemType.Clarification, "1234567890abcdef",
            CollaborationItemStatus.Resolved, "Choose model", "Use a local fixture", null,
            raisedAt, raisedAt.AddMinutes(2), "Chosen",
            [
                new("answer-2", "Final answer", raisedAt.AddMinutes(2)),
                new("answer-1", "Earlier answer", raisedAt.AddMinutes(1),
                    SupersededByAnswerId: "answer-2")
            ]);
        var delivered = new CollaborationItem(
            "cccccccc00000000", CollaborationItemType.Verify, null,
            CollaborationItemStatus.Delivered, "Verify output", "", null, raisedAt, null, " ");

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            CollaborationItemsTextView.Print([resolved, delivered], includeHistory: true,
                item => item.Id == resolved.Id ? "clarify-7" : " "));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Attention queue: 2 reach-up item(s)",
            "  [Clarification] clarify-7 status=Resolved goal=12345678",
            "    dismiss: unavailable (status=Resolved; item-id=bbbbbbbb00000000)",
            "    subject: Choose model",
            "    body: Use a local fixture",
            "    resolution: Chosen",
            "    answer-history: answer-1 retracted supersededBy=answer-2: Earlier answer",
            "    answer-history: answer-2 authoritative: Final answer",
            "    raised: 2026-10-30 05:00:00Z",
            "    resolved: 2026-10-30 05:02:00Z",
            "  [Verify] cccccccc status=Delivered goal=cross-goal",
            "    dismiss: attention dismiss --item cccccccc00000000",
            "    subject: Verify output",
            "    raised: 2026-10-30 05:00:00Z",
            "", ""), actual);
    }

    [Fact]
    public void Print_OpenItemWithoutHistory_PreservesExactText()
    {
        var raisedAt = new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero);
        var item = new CollaborationItem(
            "dddddddd00000000", CollaborationItemType.Verify, null,
            CollaborationItemStatus.Raised, "Verify text", "Exact output", null,
            raisedAt, null, null, [new("answer-1", "Hidden history", raisedAt)]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            CollaborationItemsTextView.Print([item], includeHistory: false));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Attention queue: 1 open reach-up item(s)",
            "  [Verify] dddddddd status=Raised goal=cross-goal",
            "    dismiss: attention dismiss --item dddddddd00000000",
            "    subject: Verify text",
            "    body: Exact output",
            "    raised: 2026-10-30 05:00:00Z",
            "", ""), actual);
    }
}
