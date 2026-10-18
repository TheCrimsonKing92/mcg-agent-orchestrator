using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: every marker is in a unique temporary host root.
public sealed class RemoteExecutorOccupancyCensusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "occupancy-census-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ClassedMarkerPreservesLiveness(int value)
    {
        var claimClass = (RemoteExecutorClaimClass)value;
        RemoteExecutorOccupancy.Claim(_root, "one", "attempt", 0, claimClass);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder("one"), "attempt.json")));
        Assert.Equal(claimClass.ToString(), json.RootElement.GetProperty("Class").GetString());
        Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, "one"));
        Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, "one", _ => null));
        Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, "one", pid => RemoteExecutorOccupancy.DefaultStartTime(pid)?.AddSeconds(1)));
    }

    [Fact]
    public void CensusCountsLiveClassesAndLegacyMarkersInConfigurationOrder()
    {
        foreach (var claimClass in Enum.GetValues<RemoteExecutorClaimClass>())
            RemoteExecutorOccupancy.Claim(_root, "one", claimClass.ToString(), 0, claimClass);
        RemoteExecutorOccupancy.Claim(_root, "one", "screening-x");
        RemoteExecutorOccupancy.Claim(_root, "one", "abc");
        var start = RemoteExecutorOccupancy.DefaultStartTime(Environment.ProcessId)!.Value;
        File.WriteAllText(Path.Combine(Folder("one"), "dead.json"), JsonSerializer.Serialize(new { OwnerPid = int.MaxValue, OwnerStartUtc = start }));
        File.WriteAllText(Path.Combine(Folder("one"), "recycled.json"), JsonSerializer.Serialize(new { OwnerPid = Environment.ProcessId, OwnerStartUtc = start.AddSeconds(1) }));
        var rows = RemoteExecutorOccupancyCensus.Take(_root, [("two", "retired"), ("one", "full")]);
        Assert.Equal(new RemoteExecutorOccupancyEntry("two", 0, 0, 0, 0, 0, "retired"), rows[0]);
        Assert.Equal(new RemoteExecutorOccupancyEntry("one", 1, 1, 1, 2, 1, "full"), rows[1]);
        using var legacy = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder("one"), "abc.json")));
        Assert.Equal(new[] { "OwnerPid", "OwnerStartUtc" }, legacy.RootElement.EnumerateObject().Select(field => field.Name));
    }

    [Fact]
    public void TwoFieldMarkerAndUnknownClassUseLegacyFallback()
    {
        Directory.CreateDirectory(Folder("one"));
        var start = RemoteExecutorOccupancy.DefaultStartTime(Environment.ProcessId)!.Value;
        File.WriteAllText(Path.Combine(Folder("one"), "abc.json"), JsonSerializer.Serialize(new { OwnerPid = Environment.ProcessId, OwnerStartUtc = start }));
        Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, "one"));
        Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, "one", _ => null));
        Assert.False(RemoteExecutorOccupancy.IsOccupied(_root, "one", _ => start.AddSeconds(1)));
        Assert.Equal(1, Assert.Single(RemoteExecutorOccupancyCensus.Take(_root, [("one", "full")])).Unclassified);
        File.WriteAllText(Path.Combine(Folder("one"), "screening-old.slot-1.json"), JsonSerializer.Serialize(new { OwnerPid = Environment.ProcessId, OwnerStartUtc = start, Class = "unknown" }));
        Assert.Equal(1, Assert.Single(RemoteExecutorOccupancyCensus.Take(_root, [("one", "full")])).Screening);
    }

    private string Folder(string executor) => Path.Combine(_root, ".orchestrator", "remote-executor-occupancy", executor);

    [Theory]
    [InlineData("\"sHaDoW\"", 1, 0)]
    [InlineData("\"unknown\"", 0, 1)]
    [InlineData("\"\"", 0, 1)]
    [InlineData("0", 0, 1)]
    [InlineData("\"0\"", 0, 1)]
    public void ClassParsingCannotChangeLegacyLiveness(string rawClass, int shadow, int unclassified)
    {
        RemoteExecutorOccupancy.Claim(_root, "one", "abc");
        var path = Path.Combine(Folder("one"), "abc.json");
        var marker = File.ReadAllText(path);
        File.WriteAllText(path, marker[..^1] + ",\"Class\":" + rawClass + "}");
        Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, "one"));
        var row = Assert.Single(RemoteExecutorOccupancyCensus.Take(_root, [("one", "full")]));
        Assert.Equal(shadow, row.Shadow);
        Assert.Equal(unclassified, row.Unclassified);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
