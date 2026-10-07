using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneExecutorConfigurationTestsExecutorsOnly
{
    [Xunit.Fact]
    public void PausedLanesRetainEveryExecutorFieldOnlyForExecutorsOnlyLoad()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var expected = new[] {
                new RemoteLaneExecutorEntry("one", 91, "ssh", "runner-one", "admin-one", "C:/repo/one.git", "D:/runs", 7),
                new RemoteLaneExecutorEntry("two", 123, "ssh", "runner-two", "admin-two", "E:/repo/two.git", "F:/runs", 11)
            };
            var path = Path.Combine(root, "executors.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { executors = expected.Select(entry => new {
                id = entry.Id, leaseSeconds = entry.LeaseSeconds, transport = entry.Transport,
                runnerAlias = entry.RunnerAlias, adminAlias = entry.AdminAlias, remoteRepository = entry.RemoteRepository,
                runRoot = entry.RunRoot, pollSeconds = entry.PollSeconds }), lanes = Array.Empty<string>() }));
            Assert.Equal(expected, RemoteLaneExecutorConfiguration.LoadExecutors(path));
            var routing = RemoteLaneExecutorConfiguration.Load(path);
            Assert.Equal("empty", routing.DisabledReason);
            Assert.Empty(routing.Executors);
            Assert.Empty(routing.Lanes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, "missing")]
    [Xunit.InlineData("{", "invalid")]
    [Xunit.InlineData("{\"executors\":[{\"id\":\"one\",\"transport\":\"bad\"}],\"lanes\":[]}", "invalid")]
    [Xunit.InlineData("{\"executors\":[{\"id\":\"one\"}],\"lanes\":[17]}", "invalid")]
    public void MissingOrInvalidConfigurationReturnsNoEntries(string? json, string reason)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "executors.json");
            if (json is not null) File.WriteAllText(path, json);
            Assert.Equal(reason, RemoteLaneExecutorConfiguration.Load(path).DisabledReason);
            Assert.Empty(RemoteLaneExecutorConfiguration.LoadExecutors(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
