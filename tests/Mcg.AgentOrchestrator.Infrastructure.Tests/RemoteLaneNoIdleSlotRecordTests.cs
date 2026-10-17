using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel safe: private host root, clock and fake executor; no shared repository state.
public sealed class RemoteLaneNoIdleSlotRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "no-idle-slot-" + Guid.NewGuid().ToString("N"));
    private readonly ManualRemoteLaneClock _clock = new();
    private readonly List<string> _events = [];
    private string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(_root);
    private static Check Lane(string name = "A") => new()
    {
        Name = "infrastructure tests: " + name, Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
    };

    [Fact]
    public void FullSlotsRecordOncePerLaneWithClassCensusAndEvent()
    {
        using var first = RemoteExecutorOccupancy.TryClaimExclusive(_root, "one", 0);
        using var second = RemoteExecutorOccupancy.TryClaimExclusive(_root, "one", 1);
        Assert.NotNull(first); Assert.NotNull(second);
        RemoteExecutorOccupancy.Claim(_root, "one", "shadow", 0, RemoteExecutorClaimClass.Shadow);
        RemoteExecutorOccupancy.Claim(_root, "one", "gate", 1, RemoteExecutorClaimClass.Gate);
        using var coordinator = Coordinator();
        Assert.Null(coordinator.TryClaimIdleExecutor(Lane()));
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(Ledger));
        Assert.Equal(RemoteLaneOutcomeCode.OfferSkippedNoExecutor, row.Outcome);
        Assert.Equal("", row.ExecutorId); Assert.Null(row.FaultOwner);
        Assert.Equal(Lane().Name, row.Lane); Assert.Equal("attempt", row.GateAttemptId);
        Assert.Equal(new RemoteExecutorOccupancyEntry("one", 1, 0, 1, 0, 0, "full"), Assert.Single(row.Occupancy!));
        Assert.Equal("no-idle-slot one=gate:1,focused-evidence:0,shadow:1,screening:0,unclassified:0,state:full", row.Reason);
        Assert.Contains("executor=none", Assert.Single(_events));
        Assert.Contains("outcome=offer-skipped-no-executor", Assert.Single(_events));
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Ledger)));
        Assert.Equal("offer-skipped-no-executor", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("occupancy")[0].GetProperty("shadow").GetInt32());
        Assert.Null(coordinator.TryClaimIdleExecutor(Lane()));
        Assert.Single(RemoteExecutorHealthLedger.ReadAll(Ledger));
        Assert.Null(coordinator.TryClaimIdleExecutor(Lane("B")));
        Assert.Equal(new[] { Lane().Name, Lane("B").Name }, RemoteExecutorHealthLedger.ReadAll(Ledger).Select(item => item.Lane));
    }

    [Theory]
    [InlineData("executor-busy", "backoff")]
    [InlineData("submission-failed", "retired")]
    public async Task SubmissionStateExplainsWhyExecutorIsUnavailable(string failure, string state)
    {
        var fake = new FakeRemoteLaneExecutor { Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, failure)) };
        using var coordinator = Coordinator(fake);
        var entry = Assert.IsType<RemoteLaneExecutorEntry>(coordinator.TryClaimIdleExecutor());
        await coordinator.RunRemoteAsync(Lane(), entry, TimeSpan.FromMinutes(1), default);
        Assert.Null(coordinator.TryClaimIdleExecutor(Lane("B")));
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(Ledger), row => row.Outcome == RemoteLaneOutcomeCode.OfferSkippedNoExecutor);
        Assert.Equal(state, Assert.Single(row.Occupancy!).State);
    }

    [Fact]
    public void FreeSlotAndParameterlessNullWriteNoRows()
    {
        using var coordinator = Coordinator();
        Assert.NotNull(coordinator.TryClaimIdleExecutor(Lane()));
        Assert.NotNull(coordinator.TryClaimIdleExecutor());
        Assert.Null(coordinator.TryClaimIdleExecutor());
        Assert.Empty(RemoteExecutorHealthLedger.ReadAll(Ledger));
    }

    [Fact]
    public void NoSlotRowsLeaveExistingHistoryAndReportsUnchanged()
    {
        RemoteLaneOfferSeeding.SeedRemote(Ledger, Lane().Name, Lane().Arguments[1], 100);
        var binding = new RemoteLaneBinding("one", Lane().Name, "commit", "tree", "main", "filter", "manifest");
        RemoteExecutorHealthLedger.Append(Ledger, new(_clock.GetUtcNow(), "one", "red", Lane().Name,
            RemoteLaneOutcomeCode.RemoteRed, "executor", binding, null));
        var failures = Failures();
        var history = History();
        var report = Report();
        using var coordinator = Coordinator();
        Assert.NotNull(coordinator.TryClaimIdleExecutor()); Assert.NotNull(coordinator.TryClaimIdleExecutor());
        Assert.Null(coordinator.TryClaimIdleExecutor(Lane()));
        Assert.Equal(failures, Failures());
        Assert.Equal(history, History()); Assert.Equal(report, Report());
    }

    private string Failures() => JsonSerializer.Serialize(new RemoteLaneFailureHistory().Read(Ledger)
        .OrderBy(item => item.Key).Select(item => new { Key = item.Key.ToString(), item.Value }));
    private string History() => JsonSerializer.Serialize(GoalAcceptanceVerifier.ReadRemoteLaneOfferHistory(Ledger)
        .OrderBy(item => item.Key).Select(item => new { Key = item.Key.ToString(), item.Value }));
    private string Report() => JsonSerializer.Serialize(RemoteExecutorReport.Build(["one"], RemoteExecutorHealthLedger.ReadAll(Ledger)
        .Select(row => new RemoteExecutorOutcomeRow(row.ObservedAt, row.ExecutorId, row.GateAttemptId, row.Lane,
            JsonNamingPolicy.KebabCaseLower.ConvertName(row.Outcome.ToString()), row.Reason)), []));
    private RemoteLaneCoordinator Coordinator(IRemoteLaneExecutor? executor = null) => new(
        new([new("one", 60, Slots: 2)], [Lane().Name], null),
        new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
        _root, null, executor ?? new FakeRemoteLaneExecutor(), _clock, TimeSpan.FromMilliseconds(1), null, _events.Add);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
