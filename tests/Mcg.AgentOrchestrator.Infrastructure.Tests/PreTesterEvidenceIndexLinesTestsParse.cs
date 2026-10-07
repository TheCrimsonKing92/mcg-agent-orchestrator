using Mcg.AgentOrchestrator.Core;

// Parallel-safe: markers are parsed without I/O or shared mutable state.
public sealed class PreTesterEvidenceIndexLinesTestsParse
{
    [Fact]
    public void Parse_FormattedMarker_PreservesEveryFieldAndFailingIdentity()
    {
        var entry = new PreTesterEvidenceEntry(
            "actionable-red", "abc1234", "receipt-1",
            ["Core.Tests:OneTests", "Infrastructure.Tests:TwoTests"], ["MissingTests"],
            "C:\\results\\run;one.trx",
            ["TwoTests.Fails(value: X, expected: True) (outcome: Failed)", "OneTests.Fails"]);

        var parsed = Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Parse(PreTesterEvidenceIndexLines.FormatMarker(entry)));

        Assert.Equal(entry.Outcome, parsed.Outcome);
        Assert.Equal(entry.CandidateSha, parsed.CandidateSha);
        Assert.Equal(entry.ReceiptId, parsed.ReceiptId);
        Assert.Equal(entry.Selections, parsed.Selections);
        Assert.Equal(entry.NotRun, parsed.NotRun);
        Assert.Equal(entry.ResultPath, parsed.ResultPath);
        Assert.Equal(entry.FailingTests, parsed.FailingTests);
    }

    [Theory]
    [InlineData("outcome=red; candidate_sha=abc1234")]
    [InlineData("Finding-evidence pre-tester outcome=red; candidate_sha=abc1234")]
    [InlineData("finding-evidence pre-tester outcome=red; receipt_id=r")]
    [InlineData("finding-evidence pre-tester candidate_sha=abc1234; receipt_id=r")]
    public void Parse_NonMarkerOrMissingRequiredField_ReturnsNull(string message)
    {
        Assert.Null(PreTesterEvidenceIndexLines.Parse(message));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("; result_path=none", null)]
    public void Parse_MissingOptionalFields_PreservesLatestDefaults(string suffix, string? path)
    {
        var parsed = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(
            "finding-evidence pre-tester outcome=red; candidate_sha=abc1234" + suffix));

        Assert.Equal("none", parsed.ReceiptId);
        Assert.Equal(path, parsed.ResultPath);
        Assert.Empty(parsed.Selections);
        Assert.Empty(parsed.NotRun);
        Assert.Empty(parsed.FailingTests);
    }
}
