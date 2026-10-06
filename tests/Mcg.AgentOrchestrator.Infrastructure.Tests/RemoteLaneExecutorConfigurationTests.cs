using Mcg.AgentOrchestrator.Infrastructure;

// Every test owns an independent directory and changes no ambient state.
public sealed class RemoteLaneExecutorConfigurationTests
{
    [Xunit.Fact]
    public void ValidFile_PreservesExecutorsLeasesAndLaneNames()
    {
        WithFile("""{"executors":[{"id":"one","leaseSeconds":90},{"id":"two"}],"lanes":["infrastructure tests: Cli","infrastructure tests: Shell"]}""", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Equal(new[] { new RemoteLaneExecutorEntry("one", 90), new RemoteLaneExecutorEntry("two", 60) }, configuration.Executors);
            Assert.Equal(new[] { "infrastructure tests: Cli", "infrastructure tests: Shell" }, configuration.Lanes);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, "missing")]
    [Xunit.InlineData("{", "invalid")]
    [Xunit.InlineData("[]", "invalid")]
    [Xunit.InlineData("{\"executors\":{},\"lanes\":[]}", "invalid")]
    [Xunit.InlineData("{\"executors\":[],\"lanes\":[\"lane\"]}", "empty")]
    [Xunit.InlineData("{\"executors\":[{\"id\":\"one\"}],\"lanes\":[]}", "empty")]
    [Xunit.InlineData("{\"executors\":[{\"id\":\"\"}],\"lanes\":[\"lane\"]}", "invalid")]
    [Xunit.InlineData("{\"executors\":[{\"id\":\"one\"},{\"id\":\"one\"}],\"lanes\":[\"lane\"]}", "invalid")]
    public void MissingEmptyOrMalformedFile_DisablesWithoutThrowing(string? json, string reason)
    {
        WithFile(json, path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.False(configuration.Enabled);
            Assert.Equal(reason, configuration.DisabledReason);
            Assert.Empty(configuration.Executors);
            Assert.Empty(configuration.Lanes);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData(",\"leaseSeconds\":0")]
    [Xunit.InlineData(",\"leaseSeconds\":-1")]
    public void AbsentOrNonpositiveLease_UsesSixtySeconds(string lease)
    {
        WithFile("{\"executors\":[{\"id\":\"one\"" + lease + "}],\"lanes\":[\"lane\"]}", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Equal(60, Assert.Single(configuration.Executors).LeaseSeconds);
        });
    }

    private static void WithFile(string? json, Action<string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "remote-lane-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "executors.json");
            if (json is not null) File.WriteAllText(path, json);
            check(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
