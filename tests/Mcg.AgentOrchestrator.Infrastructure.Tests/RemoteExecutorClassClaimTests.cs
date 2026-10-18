using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = RemoteExecutorSlotArbiterTests.Fixture;

// Parallel safe: private occupancy roots and fake executors, no process-wide state.
public sealed class RemoteExecutorClassClaimTests
{
    [Theory]
    [InlineData("Gate", "yield-to-gate")]
    [InlineData("FocusedEvidence", "yield-to-focused-evidence")]
    public async Task ShadowStart_RecordsDemandYieldReason(string kind, string reason)
    {
        await using var s = new RemoteTypedSlotClaimsScenarioTests.JobAccountingScenario();
        using var demand = new RemoteExecutorSlotArbiter(s.Root, "waiting", Enum.Parse<RemoteExecutorClaimClass>(kind), s.Clock);
        demand.WriteDemand(["one"]);
        var shadow = s.StartShadow(count: 1);
        await shadow.CompleteAsync(s.LocalShadowResults());
        var row = Assert.Single(s.Health());
        Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, row.Outcome);
        Assert.Equal(reason, row.Reason);
        Assert.Empty(s.Executor.ShadowHandles);
    }

    [Theory]
    [InlineData("Gate", "yield-to-gate")]
    [InlineData("FocusedEvidence", "yield-to-focused-evidence")]
    public void Demand_PreservesClassOrder(string kind, string reason)
    {
        var demandClass = Enum.Parse<RemoteExecutorClaimClass>(kind);
        using var f = new Fixture();
        using var demand = f.Arbiter("waiting", demandClass);
        demand.WriteDemand(["one"]);
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow);
        Assert.Null(shadow.TryClaimIdleExecutor(out var actual));
        Assert.Equal(reason, actual);
        using var focused = f.Coordinator("focused", RemoteExecutorClaimClass.FocusedEvidence);
        if (demandClass == RemoteExecutorClaimClass.Gate)
            Assert.Null(focused.TryClaimIdleExecutor());
        using var gate = f.Coordinator("gate", RemoteExecutorClaimClass.Gate);
        Assert.NotNull(gate.TryClaimIdleExecutor());
    }

    [Theory]
    [InlineData("Gate")]
    [InlineData("FocusedEvidence")]
    [InlineData("Screening")]
    public void IdleOnly_OtherClassMarker_BlocksShadow(string kind)
    {
        using var f = new Fixture();
        RemoteExecutorOccupancy.Claim(f.Root, "one", "other", 0, Enum.Parse<RemoteExecutorClaimClass>(kind));
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow, "idle-only", slots: 2);
        Assert.Null(shadow.TryClaimIdleExecutor(out var reason));
        Assert.Equal("idle-only", reason);
        RemoteExecutorOccupancy.Release(f.Root, "one", "other");
        Assert.NotNull(shadow.TryClaimIdleExecutor(out reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredOrDeadDemand_DoesNotBlockShadow(bool dead)
    {
        using var f = new Fixture();
        using var demand = f.Arbiter("waiting", RemoteExecutorClaimClass.Gate);
        demand.WriteDemand(["one"]);
        if (dead)
        {
            var path = Assert.Single(Directory.GetFiles(f.Folder, "*.demand"));
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["OwnerStartUtc"] = json["OwnerStartUtc"]!.GetValue<DateTimeOffset>().AddTicks(1);
            File.WriteAllText(path, json.ToJsonString());
        }
        else f.Clock.Advance(TimeSpan.FromSeconds(121));
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow);
        Assert.NotNull(shadow.TryClaimIdleExecutor(out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ParameterlessGate_Miss_WritesNoPriorityFiles()
    {
        using var f = new Fixture();
        using var gate = f.Coordinator("gate", RemoteExecutorClaimClass.Gate);
        Assert.NotNull(gate.TryClaimIdleExecutor());
        Assert.Null(gate.TryClaimIdleExecutor());
        Assert.Empty(Directory.GetFiles(f.Folder, "*.demand"));
        Assert.Empty(Directory.GetFiles(f.Folder, "*.preempt*"));
        Assert.Empty(f.Health());
    }
}
