using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ClaudeTranscriptUsageReaderTests
{
    [Fact]
    public void CountsEachAssistantMessageOnceAndIncludesBothCacheFields()
    {
        WithRoot(root =>
        {
            const string sessionId = "claude-usage-session";
            Write(root, sessionId, """
                {"type":"assistant","message":{"id":"a","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":5}},"content_block":0}
                {"type":"assistant","message":{"id":"a","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":5}},"content_block":1}
                {"type":"assistant","message":{"id":"b","usage":{"input_tokens":20,"cache_creation_input_tokens":200,"cache_read_input_tokens":2000,"output_tokens":7}}}
                """);

            var result = new ClaudeTranscriptUsageReader([root]).Read(sessionId);

            Assert.NotNull(result.Usage);
            Assert.Equal(3330, result.Usage.InputTokens);
            Assert.Equal(3000, result.Usage.CachedInputTokens);
            Assert.Equal(12, result.Usage.OutputTokens);
        });
    }

    [Fact]
    public void MissingTranscriptReportsMissingAndSessionId() => WithRoot(root =>
    {
        var result = new ClaudeTranscriptUsageReader([root]).Read("missing-session");
        Assert.Null(result.Usage);
        Assert.Contains("missing", result.UnavailableReason);
        Assert.Contains("missing-session", result.UnavailableReason);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableTranscriptReportsUnreadableAndSessionId(bool unauthorized) => WithRoot(root =>
    {
        Write(root, "unreadable-session", "{}");
        var reader = new ClaudeTranscriptUsageReader([root], _ => unauthorized
            ? throw new UnauthorizedAccessException("fixture")
            : throw new IOException("fixture"));
        var result = reader.Read("unreadable-session");
        Assert.Null(result.Usage);
        Assert.Contains("unreadable", result.UnavailableReason);
        Assert.Contains("unreadable-session", result.UnavailableReason);
    });

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"a\"}}")]
    [InlineData("{\"type\":\"assistant\",\"message\":{\"id\":\"a\",\"usage\":{}}}")]
    public void ReadableTranscriptWithoutAssistantUsageReportsNoUsage(string content) => WithRoot(root =>
    {
        Write(root, "no-usage-session", content);
        var result = new ClaudeTranscriptUsageReader([root]).Read("no-usage-session");
        Assert.Null(result.Usage);
        Assert.Contains("no-usage", result.UnavailableReason);
        Assert.Contains("no-usage-session", result.UnavailableReason);
    });

    internal static string Write(string root, string sessionId, string content)
    {
        var directory = Path.Combine(root, "undocumented", "nested");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    internal static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-claude-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
