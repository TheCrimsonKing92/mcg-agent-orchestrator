using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteFirstFocusedEvidenceModeTests
{
    [Theory]
    [InlineData("\"remote-first\"", "remote-first", null)]
    [InlineData("\"off\"", "off", null)]
    [InlineData("\"shadow\"", "shadow", null)]
    [InlineData("\"bogus\"", "off", "invalid")]
    [InlineData("1", "off", "invalid")]
    [InlineData(null, "off", null)]
    public void Parse_ModeToken_PreservesDefaultsAndRejectsUnknowns(string? token, string mode, string? fault)
    {
        var path = Path.Combine(Path.GetTempPath(), "remote-first-mode-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"executors\":[{\"id\":\"fixture\"}],\"lanes\":[]" +
                (token is null ? "}" : ",\"focusedEvidence\":{\"mode\":" + token + "}}"));
            var config = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(path);
            Assert.Null(config.DisabledReason);
            Assert.Equal(new RemoteFocusedEvidenceSettings(mode, 4, 300, fault), config.FocusedEvidence);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Flags_RemoteFirst_IsNotAToggleableShadowArm()
    {
        var path = Path.Combine(Path.GetTempPath(), "remote-first-flag-" + Guid.NewGuid().ToString("N") + ".json");
        const string json = """{"executors":[{"id":"fixture"}],"lanes":[],"focusedEvidence":{"mode":"remote-first","sampleEvery":7,"graceSeconds":42}}""";
        try
        {
            File.WriteAllText(path, json);
            Assert.Equal(new RemoteFocusedEvidenceSettings("remote-first", 7, 42),
                RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(path).FocusedEvidence);
            Assert.False(RemoteLaneExecutorFlags.Read(path, RemoteLaneExecutorFlags.FocusedEvidenceShadow));
            Assert.Equal("executors-file-invalid", Assert.Throws<InvalidOperationException>(() =>
                RemoteLaneExecutorFlags.Rewrite(Encoding.UTF8.GetBytes(json), RemoteLaneExecutorFlags.FocusedEvidenceShadow, true)).Message);
        }
        finally { File.Delete(path); }
    }
}
