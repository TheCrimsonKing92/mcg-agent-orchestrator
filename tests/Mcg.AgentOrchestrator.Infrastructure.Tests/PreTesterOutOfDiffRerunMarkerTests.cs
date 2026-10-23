using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure marker and verdict operations, no external resources.
public sealed class PreTesterOutOfDiffRerunMarkerTests
{
    private const string Identity = "ExampleTests.Fails(value: \"a,b (c)\", expected: \"quoted\")";
    private const string Key = "ExampleTests.Fails";

    [Fact]
    public void RoundTripPreservesTheoryIdentityWithCommasQuotesParentheses()
    {
        var marker = new PreTesterOutOfDiffRerunMarker("candidate", "source", "rerun-receipt", "green",
            [Identity], History(), "non-blocking");
        var parsed = Assert.IsType<PreTesterOutOfDiffRerunMarker>(PreTesterOutOfDiffRerunMarker.Parse(marker.Format()));
        Assert.Equal(marker.CandidateSha, parsed.CandidateSha);
        Assert.Equal(marker.SourceReceipt, parsed.SourceReceipt);
        Assert.Equal(marker.RerunReceipt, parsed.RerunReceipt);
        Assert.Equal(marker.Rerun, parsed.Rerun);
        Assert.Equal(marker.FailingTests, parsed.FailingTests);
        Assert.Equal(marker.FailureHistory.Keys, parsed.FailureHistory.Keys);
        Assert.Equal(marker.FailureHistory[Key], parsed.FailureHistory[Key]);
        Assert.Equal(marker.Verdict, parsed.Verdict);
    }

    [Fact]
    public void PreTesterIndexParseRejectsOutOfDiffMarker()
    {
        var marker = new PreTesterOutOfDiffRerunMarker("candidate", "source", "rerun-receipt", "green",
            [Identity], History(), "non-blocking");
        Assert.Null(PreTesterEvidenceIndexLines.Parse(marker.Format()));
        Assert.Null(PreTesterEvidenceIndexLines.Parse(PreTesterOutOfDiffRerunMarker.RequestPrefix + "candidate_sha=candidate"));
        Assert.Null(PreTesterOutOfDiffRerunMarker.Parse(PreTesterOutOfDiffRerunMarker.Prefix + "verdict=non-blocking"));
    }

    [Theory]
    [InlineData("green", false, "blocking")]
    [InlineData("red", true, "blocking")]
    [InlineData("green", true, "non-blocking")]
    [InlineData("unusable", true, "blocking")]
    public void VerdictTable(string outcome, bool hasHistory, string expected) =>
        Assert.Equal(expected, PreTesterOutOfDiffRerunMarker.DecideVerdict(outcome, [Key], hasHistory ? History() : new Dictionary<string, IReadOnlyList<string>>()));

    [Fact]
    public void EveryFailureRequiresHistory() =>
        Assert.Equal("blocking", PreTesterOutOfDiffRerunMarker.DecideVerdict("green", [Key, "NewTests.Regression"], History()));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> History() =>
        new Dictionary<string, IReadOnlyList<string>> { [Key] = new[] { "f4d70bca", "38e8c91b" } };
}
