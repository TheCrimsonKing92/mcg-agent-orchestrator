using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ApparatusRedFailureSummaryTests
{
    [Fact]
    public void SevenIdentities_FormatsFiveInInputOrderWithRemainder()
    {
        string[] identities = ["seven", "six", "five", "four", "three", "two", "one"];
        var failures = identities.Reverse().Select(identity => Failure(identity, "message")).ToArray();

        var summary = ApparatusRedFailureSummary.Format(identities, failures);

        Assert.Equal(
            "seven: message; six: message; five: message; four: message; three: message; (+2 more)",
            summary);
    }

    [Fact]
    public void MissingIdentity_ReportsUnavailable()
    {
        Assert.Equal("missing: failure message unavailable",
            ApparatusRedFailureSummary.Format(["missing"], [Failure("other", "message")]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n  \n")]
    public void BlankMessage_ReportsUnavailable(string? message)
    {
        Assert.Equal("test: failure message unavailable",
            ApparatusRedFailureSummary.Format(["test"], [Failure("test", message)]));
    }

    [Fact]
    public void DuplicateMatches_UsesFirstNonBlankLineFromFirstEntry()
    {
        var summary = ApparatusRedFailureSummary.Format(
            ["test", "test"],
            [Failure("Test", "wrong case"), Failure("test", " \r\n  first\t  failure  \r\nsecond"),
                Failure("test", "later entry")]);

        Assert.Equal("test: first failure; test: first failure", summary);
    }

    [Fact]
    public void BlankFirstMatch_DoesNotUseLaterEntry()
    {
        Assert.Equal("test: failure message unavailable",
            ApparatusRedFailureSummary.Format(["test"], [Failure("test", " "), Failure("test", "later")]));
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public void LengthBoundary_CutsCollapsedLineOnlyWhenOverLimit(int length, bool truncated)
    {
        var summary = ApparatusRedFailureSummary.Format(["test"],
            [Failure("test", "\t  " + new string('A', length) + "  \r\nsecond")]);

        Assert.Equal("test: " + new string('A', 200) + (truncated ? "..." : ""), summary);
    }

    [Fact]
    public void IdentityWithLineBreaks_EmitsSingleLine()
    {
        Assert.Equal("test  case: message", ApparatusRedFailureSummary.Format(
            ["test\r\ncase"], [Failure("test\r\ncase", "message")]));
    }

    private static ApparatusRedFailingTest Failure(string identity, string? message) => new(
        "check", identity, null, [], false, false, FailureMessage: message);
}
