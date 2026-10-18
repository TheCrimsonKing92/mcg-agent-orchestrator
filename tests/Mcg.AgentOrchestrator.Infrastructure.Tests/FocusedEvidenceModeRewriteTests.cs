using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all inputs and outputs are in-memory byte arrays.
public sealed class FocusedEvidenceModeRewriteTests
{
    [Theory]
    [InlineData("off", "off")]
    [InlineData("off", "shadow")]
    [InlineData("off", "remote-first")]
    [InlineData("shadow", "off")]
    [InlineData("shadow", "shadow")]
    [InlineData("shadow", "remote-first")]
    [InlineData("remote-first", "off")]
    [InlineData("remote-first", "shadow")]
    [InlineData("remote-first", "remote-first")]
    public void Apply_AllModePairs_PreservesBytesAndReverses(string current, string desired)
    {
        var text = "{\r\n  \"focusedEvidence\" : {\r\n    \"shadowPreemption\" : \"idle-only\",\r\n" +
            $"    \"mode\"   :  \"{current}\",\r\n" +
            "    \"sampleEvery\": 1, \"graceSeconds\" : 300\r\n  },\r\n  \"other\": {\"mode\":\"untouched\"}\r\n}\r\n";
        var original = WithBom(text);
        var expected = WithBom(text.Replace($"\"{current}\"", $"\"{desired}\"", StringComparison.Ordinal));

        var actual = FocusedEvidenceModeRewrite.Apply(original, desired);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
        Assert.Equal(original, FocusedEvidenceModeRewrite.Apply(actual, current));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"focusedEvidence\":{\"sampleEvery\":1}}")]
    [InlineData("{\"other\":{\"focusedEvidence\":{\"mode\":\"off\"}}}")]
    public void Apply_AbsentMode_ReturnsNull(string json)
    {
        Assert.Null(FocusedEvidenceModeRewrite.Apply(Encoding.UTF8.GetBytes(json), "remote-first"));
    }

    [Theory]
    [InlineData("{\"focusedEvidence\":{\"mode\":\"bogus\"}}")]
    [InlineData("{\"focusedEvidence\":{\"mode\":1}}")]
    [InlineData("{\"focusedEvidence\":{\"mode\":\"off\"},\"focusedEvidence\":{\"mode\":\"off\"}}")]
    [InlineData("{\"focusedEvidence\":{},\"focusedEvidence\":{}}")]
    [InlineData("{\"focusedEvidence\":{\"mode\":\"off\",\"mode\":\"shadow\"}}")]
    [InlineData("{\"focusedEvidence\":1}")]
    public void Apply_InvalidCurrentValueOrShape_ThrowsStableReason(string json)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            FocusedEvidenceModeRewrite.Apply(Encoding.UTF8.GetBytes(json), "remote-first"));
        Assert.Equal("executors-file-invalid", error.Message);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("Shadow")]
    [InlineData("Remote-First")]
    [InlineData("remote_first")]
    [InlineData("")]
    public void Apply_UnsupportedDesiredMode_ThrowsBeforeReadingFile(string mode)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            FocusedEvidenceModeRewrite.Apply(Encoding.UTF8.GetBytes("{"), mode));
        Assert.Equal("mode", error.ParamName);
    }

    [Theory]
    [InlineData("off", "shadow")]
    [InlineData("shadow", "off")]
    public void Apply_OffShadowPairs_MatchesExistingBoolWriter(string current, string desired)
    {
        const string preemption = "{\"focusedEvidence\": { \"shadowPreemption\" : \"idle-only\",\r\n \"mode\":\"off\" }}";
        var fixtures = new[]
        {
            ExecutorSettingIntentScenarioTests.Harness.Json.Replace("\"shadow\"", $"\"{current}\"", StringComparison.Ordinal),
            preemption.Replace("\"off\"", $"\"{current}\"", StringComparison.Ordinal)
        };
        foreach (var text in fixtures)
        {
            var original = Encoding.UTF8.GetBytes(text);
            var expected = RemoteLaneExecutorFlags.Rewrite(original,
                RemoteLaneExecutorFlags.FocusedEvidenceShadow, desired == "shadow");
            Assert.NotNull(expected);
            Assert.Equal(expected, FocusedEvidenceModeRewrite.Apply(original, desired));
        }
    }

    private static byte[] WithBom(string text) => [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)];
}
