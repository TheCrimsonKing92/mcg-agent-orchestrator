using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real git and SQLite operate only in the fixture's unique repository.
public sealed class ConductorBoardFillHostTrackedEditsHoldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dirty_main_holds_once_and_first_clean_tick_releases_and_starts(bool restart)
    {
        using var repo = new GitTrackedEditsProbeTests.RepositoryFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(repo.Root);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlog.AddAsync("Ready item", "Build the receipt");
        var clock = new PanelTestHarness.Clock { UtcNow = BoardFillReadyItemSelectorTests.Now };
        var kernel = new AgentOrchestratorKernel(clock);
        var store = new ConductorBoardFillDraftStore(ConductorBoardFillDraftStore.DefaultPath(workspace));
        var modelCalls = 0;
        var repository = new GitAuthorBriefDraftRepository(repo.Root, "main");
        var head = repo.Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(repo.Root, "seed.txt"), "dirty main");
        Assert.False(GitTrackedEditsProbe.Probe(repo.Root).Clean);
        var events = new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => clock.UtcNow);

        ConductorBoardFillHost NewHost() => new(store,
            (id, token) => AuthorBriefDraftService.Run(id, workspace,
                new((_, _) =>
                {
                    Interlocked.Increment(ref modelCalls);
                    return Task.FromResult(new WorkerProcessRunResult(0,
                        JsonSerializer.Serialize(new { kind = "stale", reason = "already implemented",
                            evidenceReferences = new[] { "seed.txt:1" } }), ""));
                }, repository), TextWriter.Null, TextWriter.Null, token, utcNow: () => clock.UtcNow),
            () => [item], (_, _) => BoardFillFailureKindSelectionTests.Ready,
            () => ConductorAutonomyPolicy.Permissive, events, () => clock.UtcNow,
            mainHead: () => head, trackedEdits: () => GitTrackedEditsProbe.Probe(repo.Root));

        var host = NewHost();
        try
        {
            host.ServiceTick(kernel);
            await PanelTestHarness.Signal(host.CurrentRound!, "dirty repository round finished");
            host.ServiceTick(kernel);
            var held = Assert.Single(store.ReadAll());
            Assert.Equal("held", held.Outcome);
            Assert.Equal("tracked-edits", held.HoldReason);
            Assert.Equal(new[] { "seed.txt" }, held.HeldPaths);
            Assert.Contains("seed.txt", held.Failure);
            Assert.Equal(0, store.StartedOnUtcDay(clock.UtcNow));
            Assert.Equal(0, store.ReadAll().Count(BoardFillFailureKind.CountsTowardItem));
            Assert.Equal(0, Volatile.Read(ref modelCalls));
            AssertHeldEvents(workspace, "started");
            using (var receipt = JsonDocument.Parse(File.ReadAllText(held.ReceiptPath!)))
            {
                Assert.Equal("tracked-edits", receipt.RootElement.GetProperty("holdReason").GetString());
                Assert.Equal("seed.txt", Assert.Single(receipt.RootElement.GetProperty("heldPaths").EnumerateArray()).GetString());
            }

            if (restart)
            {
                host.Stop();
                host = NewHost();
            }
            for (var tick = 0; tick < 3; tick++) host.ServiceTick(kernel);
            Assert.Null(host.CurrentRound);
            Assert.Equal(held.Id, Assert.Single(store.ReadAll()).Id);
            AssertHeldEvents(workspace, "started");

            repo.Git("restore", "--", "seed.txt");
            Assert.True(GitTrackedEditsProbe.Probe(repo.Root).Clean);
            host.ServiceTick(kernel);
            Assert.NotNull(host.CurrentRound);
            AssertHeldEvents(workspace, "started", "released");
            var retry = Assert.Single(store.ReadAll().Where(round => round.Outcome is null));
            Assert.Equal(item.Id, retry.BacklogItemId);
            Assert.NotEqual(held.Id, retry.Id);
            Assert.Equal(clock.UtcNow, store.ReadAll().Single(round => round.Id == held.Id).HoldReleasedAt);
            await PanelTestHarness.Signal(host.CurrentRound!, "round after clean tree finished");
            Assert.Equal(1, Volatile.Read(ref modelCalls));
            host.ServiceTick(kernel);
            Assert.Null(host.CurrentRound);
            Assert.Equal(2, store.ReadAll().Count);
            AssertHeldEvents(workspace, "started", "released");
        }
        finally { host.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unresolved_probe_preserves_a_hold_without_duplicate_events(bool throws)
    {
        using var h = new BoardFillTestHarness();
        var round = h.Store.Begin(Assert.Single(h.Items), h.Clock.UtcNow);
        h.Store.Finish(round, new("held", 1, null, null, null, [],
            TrackedEdits: new(["seed.txt"])), h.Clock.UtcNow);
        var hold = new BoardFillTrackedEditsHold(h.Store, new(h.EventsPath), () =>
            throws ? throw new IOException("probe unavailable") : (false, null));
        Assert.True(hold.Blocks(h.Clock.UtcNow));
        Assert.True(hold.Blocks(h.Clock.UtcNow));
        Assert.Single(h.Events());
        Assert.Null(Assert.Single(h.Store.ReadAll()).HoldReleasedAt);
    }

    [Fact]
    public void Release_event_failure_is_retried_after_restart_before_unblocking()
    {
        using var h = new BoardFillTestHarness();
        var round = h.Store.Begin(Assert.Single(h.Items), h.Clock.UtcNow);
        h.Store.Finish(round, new("held", 1, null, null, null, [],
            TrackedEdits: new(["seed.txt"])), h.Clock.UtcNow);
        var reject = false;
        var writer = new ConductEventLogWriter(h.EventsPath, beforeRequiredEventDrain: () =>
        {
            if (reject) throw new IOException("event store unavailable");
        });
        var hold = new BoardFillTrackedEditsHold(h.Store, writer, () => (true, null));
        hold.Enter(Assert.Single(h.Store.ReadAll()));
        reject = true;
        Assert.True(hold.Blocks(h.Clock.UtcNow));
        Assert.Equal(h.Clock.UtcNow, Assert.Single(h.Store.ReadAll()).HoldReleasedAt);
        Assert.False(Assert.Single(h.Store.ReadAll()).HoldReleaseReported);
        Assert.Single(h.Events());

        var restarted = new BoardFillTrackedEditsHold(h.Store, new(h.EventsPath), () =>
            throw new Exception("persisted release must be retried without another probe"));
        Assert.False(restarted.Blocks(h.Clock.UtcNow.AddMinutes(1)));
        Assert.True(Assert.Single(h.Store.ReadAll()).HoldReleaseReported);
        Assert.Equal(2, h.Events().Length);
        using var released = BoardFillTestHarness.Event(h.Events()[1]);
        Assert.Equal(h.Clock.UtcNow, released.RootElement.GetProperty("timestamp").GetDateTimeOffset());
        Assert.False(restarted.Blocks(h.Clock.UtcNow.AddMinutes(2)));
        Assert.Equal(2, h.Events().Length);
    }

    private static void AssertHeldEvents(OrchestratorWorkspace workspace, params string[] states)
    {
        var details = File.ReadAllLines(workspace.ConductEventsLogPath).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("eventKind").GetString() == "board-fill-held"
                ? document.RootElement.GetProperty("detail").GetString() : null;
        }).Where(detail => detail is not null).ToArray();
        Assert.Equal(states.Select(state => $"BOARD_FILL_HELD state={state} reason=tracked-edits files=seed.txt"), details);
    }
}
