using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: configuration and coordinator state are fixture-local; the child is faked.
public sealed class RemoteWorkerBuildCheckSettingsTests
{
    [Theory]
    [InlineData(null, "off", 600, null)]
    [InlineData("{}", "off", 600, null)]
    [InlineData("{\"mode\":\"remote-first\",\"remoteSeconds\":300}", "remote-first", 300, null)]
    [InlineData("{\"mode\":\"off\"}", "off", 600, null)]
    [InlineData("{\"remoteSeconds\":59}", "off", 600, "invalid")]
    [InlineData("{\"remoteSeconds\":1801}", "off", 600, "invalid")]
    [InlineData("{\"remoteSeconds\":\"x\"}", "off", 600, "invalid")]
    [InlineData("{\"mode\":\"fast\"}", "off", 600, "invalid")]
    [InlineData("{\"mode\":\"Remote-first\"}", "off", 600, "invalid")]
    [InlineData("[]", "off", 600, "invalid")]
    public void Load_OptionalBlock_DoesNotAlterLaneSettings(string? block, string mode, int seconds, string? fault)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "config.json");
            var baseline = JsonNode.Parse("""
                {"executors":[{"id":"mcg-exec-1","leaseSeconds":120}],"lanes":["one"],
                 "focusedEvidence":{"mode":"remote-first","sampleEvery":2,"graceSeconds":42}}
                """)!.AsObject();
            File.WriteAllText(path, baseline.ToJsonString());
            var expected = RemoteLaneExecutorConfiguration.LoadForWorkerBuildCheck(path);
            if (block is not null) baseline["workerBuildCheck"] = JsonNode.Parse(block);
            File.WriteAllText(path, baseline.ToJsonString());
            var actual = RemoteLaneExecutorConfiguration.LoadForWorkerBuildCheck(path);
            Assert.Equal(new RemoteWorkerBuildCheckSettings(mode, seconds, fault), actual.WorkerBuildCheck);
            Assert.Equal(expected.Enabled, actual.Enabled);
            Assert.Equal(expected.DisabledReason, actual.DisabledReason);
            Assert.True(expected.Executors.SequenceEqual(actual.Executors));
            Assert.True(expected.Lanes.SequenceEqual(actual.Lanes));
            Assert.Equal(expected.FocusedEvidence, actual.FocusedEvidence);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reconcile_RemoteMode_ExtendsOnlyRemoteBudget(bool enabled)
    {
        var fixture = new CompletionBuildEvidenceFixture();
        if (enabled)
        {
            var path = RemoteLaneExecutorConfiguration.ResolveStorePath(fixture.Task.LastProcess!.WorkingDirectory);
            Assert.Equal(Path.Combine(fixture.Root, ".orchestrator", "remote-lane-executors.json"), path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {"executors":[],"lanes":[],"workerBuildCheck":{"mode":"remote-first","remoteSeconds":300}}
                """);
        }
        fixture.Runner().RefreshLatestProcess(fixture.Kernel, fixture.GoalId, fixture.TaskId);
        Assert.Equal(fixture.Clock.UtcNow + OrchestratorBuildEvidenceCheck.DefaultTimeout + TimeSpan.FromSeconds(enabled ? 300 : 0),
            fixture.Task.CompletionBuildEvidence!.Deadline);
    }
}
