using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperCriteriaSelfCheckParserTests
{
    [Fact]
    public void ParsesEveryStatusWithOriginalIndexesAndEvidence()
    {
        var result = DeveloperCriteriaSelfCheck.Parse("""
            [{"criterion_index":0,"status":"proven","evidence":"Tests.Outcome fails without change"},{"criterion_index":3,"status":"not-owned","evidence":"Reviewer"},{"criterion_index":5,"status":"unmet","evidence":"Missing assertion"}]
            """);
        Assert.Equal(DeveloperSelfCheckFieldState.Present, result.State);
        Assert.Null(result.Reason);
        Assert.Equal(new[]
        {
            new DeveloperCriterionSelfCheck(0, DeveloperCriterionStatus.Proven, "Tests.Outcome fails without change"),
            new DeveloperCriterionSelfCheck(3, DeveloperCriterionStatus.NotOwned, "Reviewer"),
            new DeveloperCriterionSelfCheck(5, DeveloperCriterionStatus.Unmet, "Missing assertion")
        }, result.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankFieldIsMissing(string? value)
    {
        var result = DeveloperCriteriaSelfCheck.Parse(value);
        Assert.Equal(DeveloperSelfCheckFieldState.Missing, result.State);
        Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("[{", "JSON")]
    [InlineData("{}", "JSON")]
    [InlineData("null", "JSON")]
    [InlineData("[null]", "criterion_index")]
    [InlineData("[{\"status\":\"proven\",\"evidence\":\"test\"}]", "criterion_index")]
    [InlineData("[{\"criterion_index\":-1,\"status\":\"proven\",\"evidence\":\"test\"}]", "criterion_index")]
    [InlineData("[{\"criterion_index\":0,\"status\":\"done\",\"evidence\":\"test\"}]", "status")]
    [InlineData("[{\"criterion_index\":0,\"status\":\"proven\",\"evidence\":\"  \"}]", "evidence")]
    [InlineData("[\n]", "one line")]
    public void MalformedFieldReturnsSpecificReasonWithoutThrowing(string value, string reason)
    {
        var result = DeveloperCriteriaSelfCheck.Parse(value);
        Assert.Equal(DeveloperSelfCheckFieldState.Malformed, result.State);
        Assert.Empty(result.Entries);
        Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArrayAndOverlongEvidenceRemainPresent()
    {
        var empty = DeveloperCriteriaSelfCheck.Parse("[]");
        Assert.Equal(DeveloperSelfCheckFieldState.Present, empty.State);
        Assert.Empty(empty.Entries);
        var evidence = new string('x', 600) + "TAIL";
        var result = DeveloperCriteriaSelfCheck.Parse(
            $"[{{\"criterion_index\":0,\"status\":\"proven\",\"evidence\":\"{evidence}\"}}]");
        Assert.Equal(evidence, Assert.Single(result.Entries).Evidence);
    }

    [Fact]
    public void LatestBlockAndLastFieldWinWithoutOlderFallback()
    {
        var earlier = "WORKER_RESULT:\ncriteria_self_check: []\nEND_WORKER_RESULT\n";
        var missing = DeveloperCriteriaSelfCheck.ParseWorkerOutput(earlier + "WORKER_RESULT:\nfiles: none\nEND_WORKER_RESULT");
        Assert.Equal(DeveloperSelfCheckFieldState.Missing, missing.State);
        var latest = DeveloperCriteriaSelfCheck.ParseWorkerOutput(earlier +
            "WORKER_RESULT:\ncriteria_self_check: broken\n- **criteria_self_check**: []\nEND_WORKER_RESULT\ncriteria_self_check: broken");
        Assert.Equal(DeveloperSelfCheckFieldState.Present, latest.State);
        Assert.Empty(latest.Entries);
        Assert.Equal(DeveloperSelfCheckFieldState.Missing, DeveloperCriteriaSelfCheck.ParseWorkerOutput(null).State);
    }
}
