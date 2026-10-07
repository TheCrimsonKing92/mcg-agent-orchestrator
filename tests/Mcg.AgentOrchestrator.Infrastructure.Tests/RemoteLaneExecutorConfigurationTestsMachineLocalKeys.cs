using Mcg.AgentOrchestrator.Infrastructure;

// Each parsing test owns its directory and changes no ambient state.
public sealed class RemoteLaneExecutorConfigurationTestsMachineLocalKeys
{
    [Xunit.Fact]
    public void MissingKeys_LoadsEnabledWithEmptyList()
    {
        WithFile("", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Empty(configuration.MachineLocalResourceKeys);
        });
    }

    [Xunit.Fact]
    public void ListedKeys_PreservesDeclaredOrder()
    {
        WithFile(""", "machineLocalResourceKeys": ["xunit:EnvMutation", "xunit:JobAccounting"]""", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Equal(new[] { "xunit:EnvMutation", "xunit:JobAccounting" }, configuration.MachineLocalResourceKeys);
        });
    }

    [Xunit.Fact]
    public void Keys_PreservesCaseWhitespaceAndDuplicates()
    {
        WithFile(""", "machineLocalResourceKeys": [" Key ", "key", " Key "]""", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Equal(new[] { " Key ", "key", " Key " }, configuration.MachineLocalResourceKeys);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"xunit:EnvMutation\"")]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("[1]")]
    [Xunit.InlineData("[null]")]
    [Xunit.InlineData("[\"\"]")]
    [Xunit.InlineData("[\" \\t\"]")]
    [Xunit.InlineData("[\"xunit:EnvMutation\", 1]")]
    public void InvalidKeys_DisablesAndClearsParsedKeys(string keys)
    {
        WithFile(", \"machineLocalResourceKeys\": " + keys, path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.False(configuration.Enabled);
            Assert.Equal("invalid", configuration.DisabledReason);
            Assert.Empty(configuration.MachineLocalResourceKeys);
            Assert.Empty(configuration.Executors);
            Assert.Empty(RemoteLaneExecutorConfiguration.LoadExecutors(path));
        });
    }

    private static void WithFile(string keysField, Action<string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "remote-lane-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "executors.json");
            File.WriteAllText(path, "{\"executors\":[{\"id\":\"one\"}],\"lanes\":[\"lane\"]" + keysField + "}");
            check(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
