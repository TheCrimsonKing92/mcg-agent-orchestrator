using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all TRX reads are injected and owned by the test.
public sealed class PreTesterFailureSignatureReaderTests
{
    private static string Line => PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
        "actionable-red", "abc123", "receipt", [], [], null, ["B", "A"]));

    [Fact]
    public void ReadableFailuresAreAppendedInFailingTestOrder()
    {
        var pathsRead = new List<string>();
        var line = Line;
        var result = PreTesterFailureSignatureReader.AppendTo(line, "actionable-red", ["B", "A"], ["one"], path =>
        {
            pathsRead.Add(path);
            return Readable(path, new("A", "Failed", "first", null), new("B", "Failed", "second", null),
                new("unlisted", "Failed", "ignored", null));
        });

        Assert.Equal(["one"], pathsRead);
        Assert.Equal(line + "; failing_signatures=B|second%20%40%20no-frame,A|first%20%40%20no-frame", result);
        var run = PreTesterFailureSignature.ReadRun(result);
        Assert.Equal(2, run.Signatures.Count);
        Assert.Equal("first @ no-frame", run.Signatures["A"]);
        Assert.Equal("second @ no-frame", run.Signatures["B"]);
    }

    [Fact]
    public void FirstFailureAcrossOrderedPathsWins()
    {
        var pathsRead = new List<string>();
        var result = PreTesterFailureSignatureReader.AppendTo(Line, "actionable-red", ["A", "B"], ["one", "two"], path =>
        {
            pathsRead.Add(path);
            return path == "one"
                ? Readable(path, new("A", "Failed", "first", null), new("A", "Failed", "duplicate", null))
                : Readable(path, new("A", "Failed", "later", null), new("B", "Failed", "second", null));
        });

        Assert.Equal(["one", "two"], pathsRead);
        var run = PreTesterFailureSignature.ReadRun(result);
        Assert.Equal(2, run.Signatures.Count);
        Assert.Equal("first @ no-frame", run.Signatures["A"]);
        Assert.Equal("second @ no-frame", run.Signatures["B"]);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Unparseable")]
    [InlineData("Unreadable")]
    public void NonReadableStatusCannotContributeSignatures(string status)
    {
        var calls = 0;
        var line = Line;
        var result = PreTesterFailureSignatureReader.AppendTo(line, "actionable-red", ["A"], ["path"], path =>
        {
            calls++;
            return new(path, Enum.Parse<AcceptanceTrxReadStatus>(status), [new("A", "Failed", "ignored", null)]);
        });

        Assert.Equal(1, calls);
        Assert.Same(line, result);
    }

    [Fact]
    public void ExceptionAfterReadableResultDiscardsEverySignature()
    {
        var pathsRead = new List<string>();
        var line = Line;
        var result = PreTesterFailureSignatureReader.AppendTo(line, "actionable-red", ["A", "B"], ["one", "two"], path =>
        {
            pathsRead.Add(path);
            return path == "one" ? Readable(path, new AcceptanceTrxFailure("A", "Failed", "known", null))
                : throw new IOException("fixture read failure");
        });

        Assert.Equal(["one", "two"], pathsRead);
        Assert.Same(line, result);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("green")]
    [InlineData("unusable")]
    public void OtherOutcomesNeverReadTrx(string outcome)
    {
        var calls = 0;
        var line = Line;
        var result = PreTesterFailureSignatureReader.AppendTo(line, outcome, ["A"], ["path"], path =>
        {
            calls++;
            return Readable(path, new AcceptanceTrxFailure("A", "Failed", "ignored", null));
        });

        Assert.Equal(0, calls);
        Assert.Same(line, result);
    }

    [Fact]
    public void UnmatchedCaseDifferentIdentityDoesNotContribute()
    {
        var calls = 0;
        var line = Line;
        var result = PreTesterFailureSignatureReader.AppendTo(line, "actionable-red", ["A"], ["path"], path =>
        {
            calls++;
            return Readable(path, new AcceptanceTrxFailure("a", "Failed", "ignored", null));
        });

        Assert.Equal(1, calls);
        Assert.Same(line, result);
    }

    private static AcceptanceTrxReadResult Readable(string path, params AcceptanceTrxFailure[] failures) =>
        new(path, AcceptanceTrxReadStatus.Readable, failures);
}
