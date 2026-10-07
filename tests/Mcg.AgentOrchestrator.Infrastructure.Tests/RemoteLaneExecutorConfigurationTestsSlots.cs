using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its configuration folder.
public sealed class RemoteLaneExecutorConfigurationTestsSlots
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingSlots_DefaultsToOneForBothLoaders(bool ssh)
    {
        WithConfiguration(ssh, null, path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Equal(1, Assert.Single(configuration.Executors).Slots);
            Assert.Equal(1, Assert.Single(RemoteLaneExecutorConfiguration.LoadExecutors(path)).Slots);
        });
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(false, 16)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    [InlineData(true, 16)]
    public void ValidSlots_BothLoadersPreserveCapacity(bool ssh, int slots)
    {
        WithConfiguration(ssh, slots.ToString(System.Globalization.CultureInfo.InvariantCulture), path =>
        {
            var configuration = RemoteLaneExecutorConfiguration.Load(path);
            Assert.True(configuration.Enabled);
            Assert.Equal(slots, Assert.Single(configuration.Executors).Slots);
            Assert.Equal(slots, Assert.Single(RemoteLaneExecutorConfiguration.LoadExecutors(path)).Slots);
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("17")]
    [InlineData("2.5")]
    [InlineData("\"2\"")]
    [InlineData("null")]
    public void InvalidSlots_DisablesBothLoadersWithOrWithoutTransport(string slots)
    {
        foreach (var ssh in new[] { false, true })
            WithConfiguration(ssh, slots, path =>
            {
                var configuration = RemoteLaneExecutorConfiguration.Load(path);
                Assert.False(configuration.Enabled);
                Assert.Equal("invalid", configuration.DisabledReason);
                Assert.Empty(configuration.Executors);
                Assert.Empty(RemoteLaneExecutorConfiguration.LoadExecutors(path));
            });
    }

    private static void WithConfiguration(bool ssh, string? slots, Action<string> inspect)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var transport = ssh ? ", \"transport\":\"ssh\", \"runnerAlias\":\"runner\", \"adminAlias\":\"admin\", \"remoteRepository\":\"C:/repo/bare.git\"" : "";
            var capacity = slots is null ? "" : ", \"slots\":" + slots;
            var path = Path.Combine(root, "executors.json");
            File.WriteAllText(path, $$"""{"executors":[{"id":"one"{{transport}}{{capacity}}}],"lanes":["lane"]}""");
            inspect(path);
        }
        finally { Directory.Delete(root, true); }
    }
}
