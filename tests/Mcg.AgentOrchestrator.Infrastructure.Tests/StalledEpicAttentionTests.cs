using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fixture owns its database and event log; all service times are injected.
public sealed class StalledEpicAttentionTests
{
    private const string EpicId = "12345678abcdef0123456789abcdef01";
    private const string Title = "Control plane";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_stall_raises_exact_item_and_repeated_runs_are_throttled()
    {
        using var f = new Fixture(View(Slice(1, EpicPlanSliceStatus.RemainingScope),
            Slice(2, EpicPlanSliceStatus.Done), Step(3, true)));
        Assert.Equal(1, f.Surface.Service(Now));
        var item = Assert.Single(await f.Store.ListAsync());
        Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Null(item.GoalId);
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("1:RemainingScope")))[..12];
        Assert.Equal($"stalled-epic:{EpicId}:{signature}", item.CorrelationKey);
        Assert.Equal("Epic stalled: 12345678 Control plane", item.Subject);
        Assert.Equal(new[] { "Epic: 12345678 Control plane", "State: Stalled: nothing in flight or ready",
            "Open items: 1", "1. slice1 — Open: remaining scope",
            "Next: file the next slice or record a plan decision; read epic-plan 12345678" }, item.Body.Split('\n'));
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(14)));
        Assert.Equal(1, f.Calls);
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(15)));
        Assert.Equal(2, f.Calls);
        Assert.Equal(item.Id, Assert.Single(await f.Store.ListAsync()).Id);
        Assert.False(File.Exists(f.EventsPath));
    }

    [Fact]
    public async Task Lifecycle_resolves_old_sets_and_preserves_owner_acknowledgement()
    {
        using var f = new Fixture(View(Slice(1, EpicPlanSliceStatus.RemainingScope)));
        Assert.Equal(1, f.Surface.Service(Now));
        var original = Assert.Single(await f.Store.ListAsync());
        f.Candidates = [Candidate(View(Slice(1, EpicPlanSliceStatus.Ready)))];
        Assert.Equal(1, f.Surface.Service(Now.AddMinutes(15)));
        var resolved = Assert.Single(await f.Store.ListAsync());
        Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Assert.Equal("epic no longer stalled", resolved.Resolution);

        f.Candidates = [Candidate(View(Slice(1, EpicPlanSliceStatus.Failed)))];
        Assert.Equal(1, f.Surface.Service(Now.AddMinutes(30)));
        var failed = Assert.Single((await f.Store.ListAsync()).Where(IsOpen));
        Assert.NotEqual(original.CorrelationKey, failed.CorrelationKey);
        f.Candidates = [Candidate(View(Slice(1, EpicPlanSliceStatus.Parked)))];
        Assert.Equal(2, f.Surface.Service(Now.AddMinutes(45)));
        var items = await f.Store.ListAsync();
        var old = Assert.Single(items.Where(item => item.Id == failed.Id));
        Assert.Equal(CollaborationItemStatus.Resolved, old.Status);
        Assert.Equal("stalled set changed", old.Resolution);
        var parked = Assert.Single(items.Where(IsOpen));
        Assert.NotEqual(failed.CorrelationKey, parked.CorrelationKey);
        Assert.True(await f.Store.TryResolveAsync(parked.CorrelationKey!, "owner acknowledged"));
        var before = Snapshot(await f.Store.ListAsync());
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(60)));
        Assert.Equal(before, Snapshot(await f.Store.ListAsync()));
        f.Candidates = [];
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(75)));
        Assert.DoesNotContain(await f.Store.ListAsync(), IsOpen);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("empty")]
    [InlineData("in-flight")]
    [InlineData("ready")]
    [InlineData("open-step")]
    [InlineData("unplanned")]
    public async Task Non_stalled_plans_leave_the_store_equal_to_empty_baseline(string scenario)
    {
        var view = scenario switch
        {
            "complete" => View(Slice(1, EpicPlanSliceStatus.Done), Slice(2, EpicPlanSliceStatus.Superseded), Step(3, true)),
            "empty" => View(),
            "in-flight" => View(Slice(1, EpicPlanSliceStatus.InFlight), Slice(2, EpicPlanSliceStatus.Failed)),
            "ready" => View(Slice(1, EpicPlanSliceStatus.Ready), Slice(2, EpicPlanSliceStatus.Blocked)),
            "open-step" => View(Step(1, false), Slice(2, EpicPlanSliceStatus.Blocked)),
            "unplanned" => View(Slice(1, EpicPlanSliceStatus.Done)) with
                { UnplannedActiveMembers = [new(EpicId, PortfolioMemberKind.Goal, "active-goal", Now, "test")] },
            _ => throw new InvalidOperationException(scenario)
        };
        if (scenario == "unplanned") Assert.True(EpicPlanNextStep.Compute(view).IsStalled);
        Assert.False(EpicPlanNextStep.Compute(view.Items).IsStalled);
        using var f = new Fixture(view);
        var before = Snapshot(await f.Store.ListAsync());
        Assert.Empty(before);
        Assert.Equal(0, f.Surface.Service(Now));
        Assert.Equal(1, f.Calls);
        Assert.Equal(before, Snapshot(await f.Store.ListAsync()));
    }

    [Fact]
    public async Task Read_failure_is_deduplicated_and_success_allows_normal_raise()
    {
        using var f = new Fixture(View(Slice(1, EpicPlanSliceStatus.Failed)));
        f.ReadError = new IOException("portfolio unreadable");
        for (var run = 0; run < 3; run++)
        {
            Assert.Equal(0, f.Surface.Service(Now.AddMinutes(run * 15)));
            Assert.Empty(await f.Store.ListAsync());
        }
        Assert.Equal(3, f.Calls);
        var detail = Assert.Single(f.Details());
        Assert.StartsWith("EPIC_STALLED_READ_FAILED reason=IOException:", detail);
        Assert.DoesNotContain("\r", detail);
        Assert.DoesNotContain("\n", detail);
        f.ReadError = null;
        Assert.Equal(1, f.Surface.Service(Now.AddMinutes(45)));
        Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await f.Store.ListAsync()).Status);
        f.ReadError = new IOException("portfolio unreadable");
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(60)));
        Assert.Equal(2, f.Details().Length);
        Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await f.Store.ListAsync()).Status);
    }

    [Fact]
    public async Task Changed_read_errors_are_sanitized_and_leave_existing_items_untouched()
    {
        using var f = new Fixture(View(Slice(1, EpicPlanSliceStatus.Failed)));
        Assert.Equal(1, f.Surface.Service(Now));
        var before = Snapshot(await f.Store.ListAsync());
        f.Candidates = [];
        f.ReadError = new IOException("broken\r\nportfolio");
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(15)));
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(16)));
        Assert.Equal(2, f.Calls);
        Assert.Equal(before, Snapshot(await f.Store.ListAsync()));
        Assert.Equal("EPIC_STALLED_READ_FAILED reason=IOException: broken  portfolio", Assert.Single(f.Details()));
        f.ReadError = new IOException("different failure");
        Assert.Equal(0, f.Surface.Service(Now.AddMinutes(30)));
        Assert.Equal(2, f.Details().Length);
        Assert.Equal(before, Snapshot(await f.Store.ListAsync()));
    }

    [Fact]
    public async Task Missing_epic_resolves_its_open_item_and_preserves_other_correlations()
    {
        using var f = new Fixture(View(Slice(1, EpicPlanSliceStatus.Failed)));
        var unrelated = await f.Store.RaiseAsync(CollaborationItemType.Decision, null, "Other", "Body", "other:epic");
        Assert.Equal(1, f.Surface.Service(Now));
        f.Candidates = [];
        Assert.Equal(1, f.Surface.Service(Now.AddMinutes(15)));
        var items = await f.Store.ListAsync();
        Assert.Equal(unrelated.Id, Assert.Single(items.Where(IsOpen)).Id);
        Assert.Equal("epic no longer stalled", Assert.Single(items.Where(item => item.Id != unrelated.Id)).Resolution);
    }

    [Fact]
    public async Task Incomplete_items_are_signed_and_rendered_in_plan_order()
    {
        using var f = new Fixture(View(Slice(3, EpicPlanSliceStatus.Parked), Slice(2, EpicPlanSliceStatus.Superseded),
            Slice(1, EpicPlanSliceStatus.Failed), Step(4, true)));
        Assert.Equal(1, f.Surface.Service(Now));
        var item = Assert.Single(await f.Store.ListAsync());
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("1:Failed;3:Parked")))[..12];
        Assert.Equal($"stalled-epic:{EpicId}:{signature}", item.CorrelationKey);
        Assert.Equal(new[] { "Open items: 2", "1. slice1 — Failed", "3. slice3 — Parked" },
            item.Body.Split('\n').Skip(2).Take(3));
    }

    private static bool IsOpen(CollaborationItem item) => !CollaborationItemLifecycle.IsTerminal(item.Status);
    private static object[] Snapshot(IReadOnlyList<CollaborationItem> items) => items.OrderBy(item => item.Id)
        .Select(item => (object)(item.Id, item.CorrelationKey, item.Status, item.Subject, item.Body,
            item.RaisedAt, item.ResolvedAt, item.Resolution)).ToArray();
    private static StalledEpicCandidate Candidate(EpicPlanView view) => new(EpicId, Title, view);
    private static EpicPlanView View(params EpicPlanItemStatus[] items) =>
        new(new(EpicId, null, items.Select(item => item.Item).ToArray(), []), items, []);
    private static EpicPlanItemStatus Slice(int position, EpicPlanSliceStatus status) =>
        new(new(position, EpicPlanItemKind.Slice, "backlog" + position, null, false), status, "slice" + position);
    private static EpicPlanItemStatus Step(int position, bool done) =>
        new(new(position, EpicPlanItemKind.Step, null, "step" + position, done), null, "step" + position);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stalled-epic-" + Guid.NewGuid().ToString("N"));
        internal string EventsPath => Path.Combine(_root, "conduct-events.log");
        internal CollaborationItemStore Store { get; }
        internal StalledEpicAttention Surface { get; }
        internal IReadOnlyList<StalledEpicCandidate> Candidates { get; set; }
        internal Exception? ReadError { get; set; }
        internal int Calls { get; private set; }

        internal Fixture(EpicPlanView view)
        {
            Store = CollaborationItemStore.ForDirectory(_root);
            Candidates = [Candidate(view)];
            Surface = new(() =>
            {
                Calls++;
                if (ReadError is { } error) throw error;
                return Candidates;
            }, Store, new(EventsPath));
        }

        internal string[] Details() => File.ReadAllLines(EventsPath).Select(line =>
        {
            using var entry = JsonDocument.Parse(line);
            Assert.Equal("epic-stalled", entry.RootElement.GetProperty("eventKind").GetString());
            return entry.RootElement.GetProperty("detail").GetString()!;
        }).ToArray();
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
