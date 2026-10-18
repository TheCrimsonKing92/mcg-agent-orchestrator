using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel safe: all occupancy, configuration and ledger files use a private host root.
public sealed class RemoteExecutorSlotArbiterTests
{
    [Fact]
    public void PriorityFiles_AreNotOccupancy_AndExpireWithOwnerOrClock()
    {
        using var f = new Fixture();
        using var arbiter = f.Arbiter("waiting", RemoteExecutorClaimClass.Gate);
        arbiter.WriteDemand(["one"]);
        RemoteExecutorOccupancy.Claim(f.Root, "one", "shadow", 0, RemoteExecutorClaimClass.Shadow);
        var target = Assert.IsType<RemoteExecutorSlotArbiter.Target>(arbiter.RequestPreemption(["one"], "lane"));
        Assert.True(target.Owned);
        var files = Directory.GetFiles(f.Folder);
        Assert.Single(files, path => path.EndsWith(".demand"));
        Assert.Single(files, path => path.EndsWith(".preempt"));
        Assert.DoesNotContain(files.Where(path => !path.EndsWith("shadow.json")), path => path.EndsWith(".json"));
        RemoteExecutorOccupancy.Release(f.Root, "one", "shadow");
        Assert.False(RemoteExecutorOccupancy.IsOccupied(f.Root, "one"));
        Assert.Equal("Gate", Assert.Single(f.Demands()));
        f.Clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal("Gate", Assert.Single(f.Demands()));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(f.Demands());
        arbiter.WriteDemand(["one"]);
        Assert.Empty(RemoteExecutorSlotArbiter.LiveDemandClasses(f.Root, "one", f.Clock.GetUtcNow(), _ => null));
        Assert.Empty(RemoteExecutorSlotArbiter.LiveDemandClasses(f.Root, "one", f.Clock.GetUtcNow(), _ => f.Clock.GetUtcNow()));
    }

    [Fact]
    public void FocusedMiss_WritesDemand_ClaimAndDisposeRemoveOwnFiles()
    {
        using var f = new Fixture();
        using var holder = f.Coordinator("holder", RemoteExecutorClaimClass.Gate);
        Assert.NotNull(holder.TryClaimIdleExecutor());
        var focused = f.Coordinator("focused", RemoteExecutorClaimClass.FocusedEvidence);
        using (focused)
        {
            Assert.Null(focused.TryClaimIdleExecutor());
            Assert.Equal("FocusedEvidence", Assert.Single(f.Demands()));
            holder.Release("one");
            Assert.NotNull(focused.TryClaimIdleExecutor());
            Assert.Empty(f.Demands());
            Assert.Null(focused.TryClaimIdleExecutor());
            Assert.Equal("FocusedEvidence", Assert.Single(f.Demands()));
        }
        Assert.Empty(Directory.GetFiles(f.Folder, "*.demand"));
    }

    [Theory]
    [InlineData(null, "cancellable", null)]
    [InlineData("\"idle-only\"", "idle-only", null)]
    [InlineData("\"cancellable\"", "cancellable", null)]
    [InlineData("\"sometimes\"", "cancellable", "invalid")]
    [InlineData("3", "cancellable", "invalid")]
    public void PreemptionSetting_ValidatesTypeAndValue(string? json, string expected, string? fault)
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "config.json");
        File.WriteAllText(path, "{\"executors\":[{\"id\":\"one\"}],\"lanes\":[],\"focusedEvidence\":{\"mode\":\"shadow\"" +
            (json is null ? "" : ",\"shadowPreemption\":" + json) + "}}");
        var settings = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(path).FocusedEvidence;
        Assert.Equal(expected, settings.ShadowPreemption);
        Assert.Equal(fault, settings.FaultReason);
        Assert.Equal(fault is null ? "shadow" : "off", settings.Mode);
    }

    [Fact]
    public void ModeRewrite_PreservesPreemptionBytes()
    {
        const string original = "{\"focusedEvidence\": { \"shadowPreemption\" : \"idle-only\",\r\n \"mode\":\"off\" }}";
        var changed = RemoteLaneExecutorFlags.Rewrite(Encoding.UTF8.GetBytes(original), RemoteLaneExecutorFlags.FocusedEvidenceShadow, true);
        Assert.Equal(original.Replace("\"off\"", "\"shadow\""), Encoding.UTF8.GetString(changed!));
        Assert.Equal(Encoding.UTF8.GetBytes(original), RemoteLaneExecutorFlags.Rewrite(changed!, RemoteLaneExecutorFlags.FocusedEvidenceShadow, false));
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "slot-arbiter-" + Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string Folder => RemoteExecutorSlotArbiter.Folder(Root, "one");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal IReadOnlyList<string> Demands() => RemoteExecutorSlotArbiter.LiveDemandClasses(Root, "one", Clock.GetUtcNow());
        internal RemoteExecutorSlotArbiter Arbiter(string attempt, RemoteExecutorClaimClass kind) => new(Root, attempt, kind, Clock);
        internal RemoteLaneCoordinator Coordinator(string attempt, RemoteExecutorClaimClass kind,
            string mode = "cancellable", int slots = 1, Action<string>? events = null) => new(
            new([new("one", 300, Slots: slots)], [Lane().Name], null)
            { FocusedEvidence = new("shadow", 1, ShadowPreemption: mode) },
            new RemoteLaneCandidateIdentity(attempt, "goal", "commit", "tree", "main", "manifest"),
            Root, null, new FakeRemoteLaneExecutor(), Clock, TimeSpan.FromMilliseconds(1), null, events, kind);
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() => RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));
        internal static Check Lane(string name = "A") => new()
        {
            Name = "infrastructure tests: " + name, Type = "dotnet-test", Project = RemoteFirstFocusedEvidenceFixture.Project,
            Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
        };
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
