using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneExecutorConfigurationSshTests
{
    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    [Xunit.InlineData(17)]
    public void SshFieldsAndDefaults(int? poll)
    {
        var entry = ValidEntry();
        if (poll.HasValue) entry["pollSeconds"] = poll;
        var configuration = Load(entry);
        Assert.True(configuration.Enabled);
        Assert.Equal(new RemoteLaneExecutorEntry("one", 90, "ssh", "runner", "admin", "C:/repo/bare.git",
            "C:/mcg-executor", poll > 0 ? poll.Value : 10), Assert.Single(configuration.Executors));
    }

    [Xunit.Fact]
    public void ExplicitRootAndLegacyEquality()
    {
        var entry = ValidEntry();
        entry["runRoot"] = "D:/custom/root";
        Assert.Equal("D:/custom/root", Assert.Single(Load(entry).Executors).RunRoot);
        Assert.Equal(new RemoteLaneExecutorEntry("one", 90), Assert.Single(Load(new()
            { ["id"] = "one", ["leaseSeconds"] = 90 }).Executors));
    }

    [Xunit.Theory]
    [Xunit.InlineData("transport", "http")]
    [Xunit.InlineData("runnerAlias", null)]
    [Xunit.InlineData("adminAlias", null)]
    [Xunit.InlineData("remoteRepository", null)]
    [Xunit.InlineData("runnerAlias", "-runner")]
    [Xunit.InlineData("adminAlias", "-admin")]
    [Xunit.InlineData("runnerAlias", "a b")]
    [Xunit.InlineData("adminAlias", "a b")]
    [Xunit.InlineData("remoteRepository", "C:/a b")]
    [Xunit.InlineData("remoteRepository", "C:/a\"b")]
    [Xunit.InlineData("remoteRepository", "repo/bare")]
    [Xunit.InlineData("runRoot", "C:/a b")]
    [Xunit.InlineData("runRoot", "C:/a\"b")]
    [Xunit.InlineData("runRoot", "root/path")]
    [Xunit.InlineData("pollSeconds", "ten")]
    public void InvalidEntryDisablesWholeFile(string field, string? value)
    {
        var entry = ValidEntry();
        if (value is null) entry.Remove(field); else entry[field] = value;
        var configuration = Load(entry);
        Assert.False(configuration.Enabled);
        Assert.Equal("invalid", configuration.DisabledReason);
        Assert.Empty(configuration.Executors);
        Assert.Empty(configuration.Lanes);
    }

    private static Dictionary<string, object?> ValidEntry() => new()
    {
        ["id"] = "one", ["leaseSeconds"] = 90, ["transport"] = "ssh", ["runnerAlias"] = "runner",
        ["adminAlias"] = "admin", ["remoteRepository"] = "C:/repo/bare.git"
    };
    private static RemoteLaneExecutorConfiguration Load(Dictionary<string, object?> entry)
    {
        var directory = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "executors.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { executors = new[] { entry }, lanes = new[] { "lane" } }));
            return RemoteLaneExecutorConfiguration.Load(path);
        }
        finally { Directory.Delete(directory, true); }
    }
}
