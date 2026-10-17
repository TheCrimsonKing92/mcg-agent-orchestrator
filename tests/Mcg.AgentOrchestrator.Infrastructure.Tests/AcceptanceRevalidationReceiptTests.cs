using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class AcceptanceRevalidationReceiptTests
{
    [Fact]
    public void Format_TryParse_RoundTripsEveryOutcomeAndField()
    {
        foreach (var outcome in new[] { "pending", "landed", "regate" })
        {
            var expected = new AcceptanceRevalidationReceipt("source-id", "old-main", "new-main", outcome,
                outcome == "pending" ? "disjoint" : outcome == "landed" ? "revalidated" : "overlap",
                ["a tests", "commas,equals=percent%plus+", "+12"], ["b tests"], 17, outcome == "landed" ? "new-id" : null);

            Assert.True(AcceptanceRevalidationReceipt.TryParse(expected.Format(), out var actual));

            Assert.NotNull(actual);
            Assert.Equal(expected.SourceReceiptId, actual.SourceReceiptId);
            Assert.Equal(expected.SourceMainSha, actual.SourceMainSha);
            Assert.Equal(expected.CurrentMainSha, actual.CurrentMainSha);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.Reason, actual.Reason);
            Assert.Equal(expected.AffectedChecks, actual.AffectedChecks);
            Assert.Equal(expected.UnaffectedChecks, actual.UnaffectedChecks);
            Assert.Equal(expected.LandedPathCount, actual.LandedPathCount);
            Assert.Equal(expected.RevalidationReceiptId, actual.RevalidationReceiptId);
        }
    }

    [Fact]
    public void Format_LongLists_AreBoundedWithAnOmittedCount()
    {
        var names = Enumerable.Range(0, 40).Select(index => $"infrastructure tests: lengthy check number {index}").ToArray();
        var receipt = new AcceptanceRevalidationReceipt("source", new string('a', 40), new string('b', 40),
            "pending", "disjoint", names, names, 8, null);

        var line = receipt.Format();

        Assert.True(line.Length <= 1024, $"Receipt was {line.Length} characters.");
        Assert.Contains("+", line);
        Assert.True(AcceptanceRevalidationReceipt.TryParse(line, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(receipt.SourceReceiptId, parsed.SourceReceiptId);
        Assert.Equal(receipt.CurrentMainSha, parsed.CurrentMainSha);
        Assert.True(parsed.AffectedChecks.Count + parsed.UnaffectedChecks.Count < 80);
    }

    [Fact]
    public void TryParse_RejectsMissingPrefixOrSource()
    {
        var line = new AcceptanceRevalidationReceipt("source", "old", "current", "regate", "unknown", [], [], 0, null).Format();
        Assert.False(AcceptanceRevalidationReceipt.TryParse(line.Replace("ACCEPTANCE_REVALIDATION ", ""), out _));
        Assert.False(AcceptanceRevalidationReceipt.TryParse(line.Replace("source=source ", ""), out _));
    }
}
