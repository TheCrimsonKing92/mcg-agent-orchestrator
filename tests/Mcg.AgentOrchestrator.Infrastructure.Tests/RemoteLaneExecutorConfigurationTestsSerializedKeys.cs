using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: parsing uses a private directory and no ambient state.
public sealed class RemoteLaneExecutorConfigurationTestsSerializedKeys
{
    [Fact]
    public void MissingKeys_LoadsEnabledWithEmptyList()
    {
        WithFile("", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Empty(configuration.ExecutorSerializedResourceKeys);
        });
    }

    [Fact]
    public void ListedKeys_PreservesOrderCaseWhitespaceAndDuplicates()
    {
        WithFile(""", "executorSerializedResourceKeys": ["xunit:JobAccounting", " Key ", "key", " Key "]""", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Equal(new[] { "xunit:JobAccounting", " Key ", "key", " Key " }, configuration.ExecutorSerializedResourceKeys);
            Assert.Empty(configuration.MachineLocalResourceKeys);
        });
    }

    [Fact]
    public void MachineLocalOnly_PreservesExistingListAndLeavesSerializedEmpty()
    {
        WithFile(""", "machineLocalResourceKeys": ["xunit:EnvMutation", "xunit:JobAccounting"]""", path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Empty(configuration.ExecutorSerializedResourceKeys);
            Assert.Equal(new[] { "xunit:EnvMutation", "xunit:JobAccounting" }, configuration.MachineLocalResourceKeys);
        });
    }

    [Theory]
    [InlineData("\"xunit:JobAccounting\"")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\" \\t\"]")]
    [InlineData("[\"xunit:JobAccounting\", 1]")]
    public void InvalidKeys_DisablesConfiguration(string keys)
    {
        WithFile(", \"executorSerializedResourceKeys\": " + keys, path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.False(configuration.Enabled);
            Assert.Equal("invalid", configuration.DisabledReason);
            Assert.Empty(configuration.ExecutorSerializedResourceKeys);
            Assert.Empty(configuration.Executors);
        });
    }

    private static void WithFile(string keysField, Action<string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "remote-serialized-keys-" + Guid.NewGuid().ToString("N"));
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
