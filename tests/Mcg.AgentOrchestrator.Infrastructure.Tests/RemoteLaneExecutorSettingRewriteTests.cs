using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: literal byte inputs and a private temporary workspace for parser checks.
public sealed class RemoteLaneExecutorSettingRewriteTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void Slots_PropertyOrderAndEncoding_PreservesEveryOtherByte(bool idLast, bool crlf, bool bom)
    {
        var entry = idLast ? "{ \"slots\": 3, \"id\": \"target\" }" : "{ \"id\": \"target\", \"slots\": 3 }";
        var input = "{\n  \"executors\": [" + entry + ", {\"id\":\"other\",\"slots\":2}],\n  \"lanes\":[\"*\"], \"focusedEvidence\":{\"mode\":\"shadow\",\"sampleEvery\":1,\"graceSeconds\":300}\n}";
        if (crlf) input = input.Replace("\n", "\r\n", StringComparison.Ordinal);
        var original = Bytes(input, bom);
        var rewritten = RemoteLaneExecutorSettingRewrite.RewriteSlots(original, "target", 16);
        Assert.NotNull(rewritten);
        Assert.Equal(Bytes(input.Replace("\"slots\": 3", "\"slots\": 16", StringComparison.Ordinal), bom), rewritten);
        AssertLoadChange(original, rewritten, "target", 16);
    }

    [Fact]
    public void Slots_MissingProperty_ReturnsNull()
    {
        Assert.Null(RemoteLaneExecutorSettingRewrite.RewriteSlots("{\"executors\":[{\"id\":\"target\"}]}"u8.ToArray(), "target", 2));
    }

    [Theory]
    [InlineData("{\"executors\":[{\"id\":\"other\",\"slots\":2}]}", "executor-not-found")]
    [InlineData("{\"executors\":[{\"id\":\"target\",\"slots\":2},{\"slots\":3,\"id\":\"target\"}]}", "executors-file-invalid")]
    [InlineData("{\"executors\":[{\"id\":\"target\",\"slots\":\"2\"}]}", "executors-file-invalid")]
    [InlineData("{\"executors\":[{\"id\":\"target\",\"slots\":2.5}]}", "executors-file-invalid")]
    [InlineData("{\"executors\":[{\"id\":\"target\",\"slots\":2,\"slots\":3}]}", "executors-file-invalid")]
    [InlineData("{\"executors\":{}}", "executors-file-invalid")]
    [InlineData("{}", "executors-file-invalid")]
    [InlineData("{", "executors-file-invalid")]
    public void Slots_InvalidInput_ThrowsExactReason(string input, string reason)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            RemoteLaneExecutorSettingRewrite.RewriteSlots(Encoding.UTF8.GetBytes(input), "target", 2));
        Assert.Equal(reason, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sample_NestedLookalikes_ChangesOnlyDirectValueAndKeepsBom(bool bom)
    {
        const string input = "{\"executors\":[{\"id\":\"target\",\"slots\":3}],\"lanes\":[\"*\"],\"focusedEvidence\":{\"unknown\":{\"sampleEvery\":1},\"mode\":\"shadow\",\"sampleEvery\": 1,\"graceSeconds\":300},\"unknown\":{\"focusedEvidence\":{\"sampleEvery\":1}}}";
        var original = Bytes(input, bom);
        var rewritten = RemoteLaneExecutorSettingRewrite.RewriteSampleEvery(original, 12);
        Assert.NotNull(rewritten);
        Assert.Equal(Bytes(input.Replace("\"sampleEvery\": 1", "\"sampleEvery\": 12", StringComparison.Ordinal), bom), rewritten);
        AssertLoadChange(original, rewritten, sample: 12);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"focusedEvidence\":{\"unknown\":{\"sampleEvery\":1}}}")]
    public void Sample_AbsentDirectProperty_ReturnsNull(string input)
    {
        Assert.Null(RemoteLaneExecutorSettingRewrite.RewriteSampleEvery(Encoding.UTF8.GetBytes(input), 4));
    }

    [Theory]
    [InlineData("{\"focusedEvidence\":{\"sampleEvery\":1,\"sampleEvery\":2}}")]
    [InlineData("{\"focusedEvidence\":{\"sampleEvery\":1},\"focusedEvidence\":{\"sampleEvery\":2}}")]
    [InlineData("{\"focusedEvidence\":{\"sampleEvery\":\"1\"}}")]
    [InlineData("{\"focusedEvidence\":[]}")]
    public void Sample_DuplicateOrInvalidValue_ThrowsExactReason(string input)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            RemoteLaneExecutorSettingRewrite.RewriteSampleEvery(Encoding.UTF8.GetBytes(input), 4));
        Assert.Equal("executors-file-invalid", error.Message);
    }

    private static byte[] Bytes(string input, bool bom) =>
        (bom ? new byte[] { 0xef, 0xbb, 0xbf } : []).Concat(Encoding.UTF8.GetBytes(input)).ToArray();

    private static void AssertLoadChange(byte[] beforeBytes, byte[] afterBytes,
        string? executorId = null, int? slots = null, int? sample = null)
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        File.WriteAllBytes(fixture.Path, beforeBytes);
        var before = fixture.Load();
        File.WriteAllBytes(fixture.Path, afterBytes);
        ExecutorSettingIntentScenarioTests.AssertEquivalent(before, fixture.Load(), executorId, slots, sample: sample);
    }
}
