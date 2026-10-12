using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: uses local values and injected readers only; no TRX files are created.
public sealed class ActionableCandidateRedFailureDetailRendererTests
{
    private const string Message = "retry message";
    private const string ReceiptId = "receipt-1";
    private const string TrxPath = "candidate.trx";
    private const string Identity = "ExampleTests.Fails";

    [Fact]
    public void Append_ReadableFailure_PreservesExactMessageAndStack()
    {
        var reads = 0;
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, [Identity], [TrxPath], 30,
            _ => throw new InvalidOperationException("Readable status must not be described"),
            path =>
            {
                reads++;
                Assert.Equal(TrxPath, path);
                return new(path, AcceptanceTrxReadStatus.Readable,
                    [new(Identity, "Failed", "failure message", "at First()\r\nat Second()")]);
            });

        Assert.Equal(1, reads);
        Assert.Equal(Detail($"[FAIL] {Identity} (Failed)", "failure message",
            "at First()", "at Second()"), result);
    }

    [Fact]
    public void Append_LongMessageAndStack_PreservesBothTruncationLimits()
    {
        var stackLines = Enumerable.Range(1, 15).Select(i => $"at Frame{i}()").ToArray();
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, [Identity], [TrxPath], 30, _ => "unused",
            path => new(path, AcceptanceTrxReadStatus.Readable,
                [new(Identity, "Failed", new string('m', 2100), string.Join("\n", stackLines))]));

        var expectedLines = new[]
        {
            $"[FAIL] {Identity} (Failed)", new string('m', 2000) + "... [message truncated]"
        }.Concat(stackLines.Take(10)).Append("... (5 more stack lines)").ToArray();
        Assert.Equal(Detail(expectedLines), result);
    }

    [Fact]
    public void Append_NoPaths_ReportsReceiptAndMissingIdentityWithoutReading()
    {
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, [Identity], null, 30,
            _ => throw new InvalidOperationException("No status should be described"),
            _ => throw new InvalidOperationException("No path should be read"));

        Assert.Equal(Detail(
            $"detail unavailable: no TRX path was recorded for receipt {ReceiptId}",
            $"detail unavailable: no failure detail found for {Identity}"), result);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Unreadable")]
    [InlineData("Unparseable")]
    public void Append_NonReadableResult_UsesInjectedDescription(string statusName)
    {
        var status = Enum.Parse<AcceptanceTrxReadStatus>(statusName);
        var reads = 0;
        var descriptions = 0;
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, [Identity], [TrxPath], 30,
            actualStatus =>
            {
                descriptions++;
                Assert.Equal(status, actualStatus);
                return "injected marker";
            },
            path =>
            {
                reads++;
                Assert.Equal(TrxPath, path);
                return new(path, status, []);
            });

        Assert.Equal(1, reads);
        Assert.Equal(1, descriptions);
        Assert.Equal(Detail($"detail unavailable: injected marker at {TrxPath}",
            $"detail unavailable: no failure detail found for {Identity}"), result);
    }

    [Fact]
    public void Append_ReaderThrows_ReportsExceptionUnderOriginalHeader()
    {
        var reads = 0;
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, [Identity], [TrxPath], 30, _ => "unused",
            _ =>
            {
                reads++;
                throw new InvalidOperationException("boom");
            });

        Assert.Equal(1, reads);
        Assert.Equal(Detail("detail unavailable: System.InvalidOperationException: boom"), result);
    }

    [Fact]
    public void Append_FiveFailuresWithCapTwo_EmitsTwoBlocksAndOmissionSummary()
    {
        var identities = Enumerable.Range(1, 5).Select(i => $"ExampleTests.Fails{i}").ToArray();
        var failures = identities.Select(identity =>
            new AcceptanceTrxFailure(identity, "Failed", "message", "stack")).ToArray();
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, identities, [TrxPath], 2, _ => "unused",
            path => new(path, AcceptanceTrxReadStatus.Readable, failures));

        Assert.Equal(Detail($"3 more failures omitted - see {TrxPath}",
            $"[FAIL] {identities[0]} (Failed)", "message", "stack",
            $"[FAIL] {identities[1]} (Failed)", "message", "stack"), result);
        Assert.Equal(2, result.Split("[FAIL] ", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, result.Split($"3 more failures omitted - see {TrxPath}",
            StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Append_UnrequestedTrxFailure_ReportsOnlyMissingRequestedIdentity()
    {
        var reads = 0;
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, [Identity], [TrxPath], 30, _ => "unused",
            path =>
            {
                reads++;
                return new(path, AcceptanceTrxReadStatus.Readable,
                    [new("OtherTests.Fails", "Failed", "unrequested message", "unrequested stack")]);
            });

        Assert.Equal(1, reads);
        Assert.Equal(Detail($"detail unavailable: no failure detail found for {Identity}"), result);
        Assert.DoesNotContain("[FAIL] ", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_BlankAndDuplicateIdentities_EmitsOneRequestedFailure()
    {
        var reads = 0;
        var result = ActionableCandidateRedFailureDetailRenderer.Append(
            Message, ReceiptId, ["", " \t", Identity, Identity], [TrxPath], 30, _ => "unused",
            path =>
            {
                reads++;
                return new(path, AcceptanceTrxReadStatus.Readable,
                    [new(Identity, "Failed", "message", "stack")]);
            });

        Assert.Equal(1, reads);
        Assert.Equal(Detail($"[FAIL] {Identity} (Failed)", "message", "stack"), result);
    }

    private static string Detail(params string[] lines) =>
        Message + Environment.NewLine + Environment.NewLine +
        $"Candidate failure detail (receipt {ReceiptId}):" + Environment.NewLine +
        string.Join(Environment.NewLine, lines);
}
