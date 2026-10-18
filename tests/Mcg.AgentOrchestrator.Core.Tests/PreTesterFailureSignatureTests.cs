using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure string transforms with no shared state.
public sealed class PreTesterFailureSignatureTests
{
    [Fact]
    public void Describe_IncidentFailureUsesFirstMessageAndSourceFrame()
    {
        var description = PreTesterFailureSignature.Describe(
            "\r\n  Assert.IsType() Failure: Value is null  \r\nSecond line",
            "at Synthetic.First()\r\n at X() in C:\\fixture\\tests\\TesterRedLabelledInconclusiveScenarioTests.cs:line 40\r\n" +
            " at Y() in /other/File.cs:line 280");

        Assert.Equal("Assert.IsType() Failure: Value is null @ TesterRedLabelledInconclusiveScenarioTests.cs:40",
            description);
    }

    [Fact]
    public void Describe_SyntheticFramesHaveNoSourceFrame() =>
        Assert.Equal("failed @ no-frame", PreTesterFailureSignature.Describe(
            "failed", "at Synthetic.First()\nat Synthetic.Second()"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t\r ")]
    public void Describe_AbsentMessageHasExplicitFallback(string? message) =>
        Assert.Equal("no-message @ no-frame", PreTesterFailureSignature.Describe(message, null));

    [Fact]
    public void Describe_TrimsThenCutsFirstLineAtTwoHundredCharacters() =>
        Assert.Equal(new string('x', 200) + " @ File.cs:7", PreTesterFailureSignature.Describe(
            "  " + new string('x', 300) + " \rNext", "at X() in /fixture/File.cs:line 7"));

    [Fact]
    public void Describe_PreservesLeadingSpaceNeededToRecognizeSourceFrame() =>
        Assert.Equal("failure @ File.cs:7", PreTesterFailureSignature.Describe(
            "failure", " in /fixture/File.cs:line 7"));

    [Fact]
    public void AppendField_EmptyListPreservesInput() =>
        Assert.Same("unchanged", PreTesterFailureSignature.AppendField("unchanged", []));

    [Fact]
    public void AppendField_RoundTripsDelimitersWithoutChangingExistingFields()
    {
        const string identity = "Class.Method(control: \"green\")";
        const string signature = "message,with;all|delimiters=here @ File.cs:40";
        var line = PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
            "actionable-red", "abc123", "receipt", ["selection"], ["not-run"], "result path", [identity]));

        var appended = PreTesterFailureSignature.AppendField(line, [new(identity, signature)]);
        var run = PreTesterFailureSignature.ReadRun(appended);

        Assert.Equal([identity], run.FailingTests);
        Assert.Equal(signature, Assert.Single(run.Signatures).Value);
        Assert.Equal(identity, Assert.Single(run.Signatures).Key);
        var before = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(line));
        var after = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(appended));
        Assert.Equal(before.Outcome, after.Outcome);
        Assert.Equal(before.CandidateSha, after.CandidateSha);
        Assert.Equal(before.ReceiptId, after.ReceiptId);
        Assert.Equal(before.Selections, after.Selections);
        Assert.Equal(before.NotRun, after.NotRun);
        Assert.Equal(before.ResultPath, after.ResultPath);
        Assert.Equal(before.FailingTests, after.FailingTests);
    }

    [Fact]
    public void ReadRun_MalformedEntriesAreSkippedAndFirstDuplicateWins()
    {
        var line = PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
            "actionable-red", "abc123", "receipt", [], [], null, ["A", "B"]));
        var run = PreTesterFailureSignature.ReadRun(line +
            "; failing_signatures=bad,|empty,empty|,too|many|parts,A|first,A|second,B|valid");

        Assert.Equal(["A", "B"], run.FailingTests);
        Assert.Equal(2, run.Signatures.Count);
        Assert.Equal("first", run.Signatures["A"]);
        Assert.Equal("valid", run.Signatures["B"]);
        Assert.Empty(PreTesterFailureSignature.ReadRun(line).Signatures);
    }

    [Fact]
    public void ReadRun_UnrecognizedLineHasNoNamesOrSignatures()
    {
        var run = PreTesterFailureSignature.ReadRun("unrecognized; failing_signatures=A|value");

        Assert.Empty(run.FailingTests);
        Assert.Empty(run.Signatures);
    }
}
