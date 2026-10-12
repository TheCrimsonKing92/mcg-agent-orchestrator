using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each test owns its temporary directory and file handles.
public sealed class CodexRawAuditStreamTests
{
    [Fact(DisplayName = "Null and missing stdout audit paths yield no evidence")]
    public void NullAndMissingPathsYieldNothing()
    {
        using var fixture = new AuditFixture();
        Assert.Empty(CodexRawAuditStream.ReadLines(null));
        Assert.Empty(CodexRawAuditStream.ReadLines(fixture.OutputPath));
        Assert.False(CodexRawAuditStream.TryGetFailedTurnMessage(null, out var nullMessage));
        Assert.False(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var missingMessage));
        Assert.Equal(string.Empty, nullMessage);
        Assert.Equal(string.Empty, missingMessage);
    }

    [Fact(DisplayName = "The literal audit sibling preserves capacity records and the last nested message")]
    public void CapacityAuditReturnsLinesAndLastFailedMessage()
    {
        using var fixture = new AuditFixture();
        string[] lines =
        [
            "{\"type\":\"thread.started\",\"thread_id\":\"t\"}",
            "{\"type\":\"turn.started\"}",
            "{\"type\":\"error\",\"message\":\"Selected model is at capacity. Please try a different model.\"}",
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"Selected model is at capacity. Please try a different model.\"}}"
        ];
        fixture.Write(string.Join("\n", lines));
        Assert.Equal(lines, CodexRawAuditStream.ReadLines(fixture.OutputPath).ToArray());
        Assert.True(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var message));
        Assert.Equal("Selected model is at capacity. Please try a different model.", message);
    }

    [Theory(DisplayName = "The last failed record wins and malformed or unrelated records are ignored")]
    [InlineData("type")]
    [InlineData("kind")]
    public void LastFailedRecordSuppliesNestedMessage(string discriminator)
    {
        using var fixture = new AuditFixture();
        fixture.Write("{\"type\":\"error\",\"message\":\"earlier cause\"}\n" +
            $"{{\"{discriminator}\":\"turn.failed\",\"error\":{{\"message\":\"terminal cause\"}}}}\n" +
            "malformed json\n[]\n{\"type\":\"item.completed\",\"message\":\"ignore this\"}");

        Assert.True(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var message));
        Assert.Equal("terminal cause", message);
    }

    [Theory(DisplayName = "A terminal failed record without usable text clears earlier display evidence")]
    [InlineData("{\"type\":\"turn.failed\"}")]
    [InlineData("{\"type\":\"error\",\"message\":42,\"error\":null}")]
    [InlineData("{\"type\":\"turn.failed\",\"message\":\" \",\"error\":{\"message\":\"\\r\\n\"}}")]
    public void LastFailedRecordWithoutMessageReturnsFalse(string lastRecord)
    {
        using var fixture = new AuditFixture();
        fixture.Write("{\"type\":\"error\",\"message\":\"earlier cause\"}\n" + lastRecord);

        Assert.False(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var message));
        Assert.Equal(string.Empty, message);
    }

    [Theory(DisplayName = "Top-level text takes precedence and blank text falls back to nested text")]
    [InlineData("top-level cause", "top-level cause")]
    [InlineData(" ", "nested cause")]
    public void TopLevelMessagePrecedence(string topLevel, string expected)
    {
        using var fixture = new AuditFixture();
        fixture.Write(JsonSerializer.Serialize(new
        {
            type = "turn.failed", message = topLevel, error = new { message = "nested cause" }
        }));

        Assert.True(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var message));
        Assert.Equal(expected, message);
    }

    [Fact(DisplayName = "Display messages collapse controls and remain bounded to 200 characters")]
    public void MessageIsSingleLineAndTruncated()
    {
        using var fixture = new AuditFixture();
        fixture.Write(JsonSerializer.Serialize(new
        {
            type = "error", message = " \r\nfirst\r\n\t\0second " + new string('x', 220) + " \n"
        }));

        Assert.True(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var message));
        Assert.Equal(("first second " + new string('x', 220))[..200], message);
        Assert.Equal(200, message.Length);
        Assert.DoesNotContain(message, char.IsControl);
    }

    [Fact(DisplayName = "Completed and nonterminal records supply no failed-turn display message")]
    public void CompletedRecordsHaveNoFailedMessage()
    {
        using var fixture = new AuditFixture();
        fixture.Write("{\"type\":\"turn.started\"}\n{\"type\":\"turn.completed\",\"message\":\"Selected model is at capacity\"}");

        Assert.Equal(2, CodexRawAuditStream.ReadLines(fixture.OutputPath).Count());
        Assert.False(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var message));
        Assert.Equal(string.Empty, message);
    }

    [Fact(DisplayName = "A writer sharing the audit file can coexist with reads")]
    public void AuditReadUsesSharedAccess()
    {
        using var fixture = new AuditFixture();
        fixture.Write("{\"type\":\"turn.failed\",\"message\":\"shared cause\"}");
        using var writer = new FileStream(fixture.OutputPath + ".jsonl", FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.Single(CodexRawAuditStream.ReadLines(fixture.OutputPath));
        Assert.True(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out var message));
        Assert.Equal("shared cause", message);
    }

    [Fact(DisplayName = "An exclusively held audit file contributes no evidence")]
    public void UnreadableAuditYieldsNothing()
    {
        using var fixture = new AuditFixture();
        fixture.Write("{\"type\":\"turn.failed\",\"message\":\"hidden cause\"}");
        using var exclusive = new FileStream(fixture.OutputPath + ".jsonl", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Empty(CodexRawAuditStream.ReadLines(fixture.OutputPath));
        Assert.False(CodexRawAuditStream.TryGetFailedTurnMessage(fixture.OutputPath, out _));
    }

    private sealed class AuditFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "codex-audit-" + Guid.NewGuid().ToString("N"));

        public AuditFixture() => Directory.CreateDirectory(directory);

        public string OutputPath => Path.Combine(directory, "worker.out.log");

        public void Write(string audit) => File.WriteAllText(OutputPath + ".jsonl", audit);

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
