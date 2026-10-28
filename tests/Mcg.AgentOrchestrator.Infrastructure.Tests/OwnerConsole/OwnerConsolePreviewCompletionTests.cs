using System.Drawing;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

// Parallel-safe: all gates, queries, publication queues and uninitialized views are instance-owned.
public sealed class OwnerConsolePreviewCompletionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pending")]
    [InlineData("passed")]
    public async Task InitialLayout_BeforePublication_PreservesPreviewAndDetail(string expected)
    {
        var text = await RenderControlledViewAsync(expected, completeLayoutBeforeLoad: true);
        Assert.Contains("Verified: " + expected, text);
    }

    [Fact]
    public async Task OverlappingLayout_AfterPreviewLoad_ReproducesLoadingAssertion()
    {
        var text = await RenderControlledViewAsync("pending", completeLayoutBeforeLoad: false);
        Assert.Contains("Verified: loading", text);
        // The identical assertion used by the repaired fixture must reject this schedule.
        var failure = Record.Exception(() => Assert.Contains("Verified: pending", text));
        Assert.NotNull(failure);
        Assert.Contains("Verified: pending", failure.Message);
        Assert.DoesNotContain("Verified: passed", text);
    }

    // Acceptance RED control: in InitialLayout_BeforePublication_PreservesPreviewAndDetail,
    // change only completeLayoutBeforeLoad: true to false. Its unchanged final Contains must
    // fail with loading content (both rows); restore true for GREEN. No production mutation.
    // This controls a 30 -> 100 column resize to make TextChanging observable; it establishes
    // the overlapping uninitialized-fixture contract, not the historical 100-column interleaving.
    private static async Task<string> RenderControlledViewAsync(string expected, bool completeLayoutBeforeLoad)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("preview-completion"), "Preview completion",
            [new(TaskId.New(), "Build", AgentRole.Developer)]);
        var candidate = new string('a', 40);
        var basis = new string('b', 40);
        var at = DateTimeOffset.UnixEpoch;
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        snapshot = snapshot with
        {
            Tasks = snapshot.Tasks.Select(task => task with
            {
                LastDispatch = new TaskDispatchSnapshot("worker", "worker", "artifact", at,
                    BaseCommit: basis, ResultCommit: candidate)
            }).ToArray()
        };
        var queries = new ControlledQueries(snapshot);
        var summary = new OwnerGoalChangeSummaryQuery(acceptance: (id, head, _, _) =>
        {
            Assert.Equal(goal.Id, id);
            Assert.Equal(candidate, head);
            return Task.FromResult(new OwnerGoalAcceptanceEvidence(basis, expected == "passed"
                ? new("controlled-acceptance", id, "acceptance", GoalOperationStatus.Completed, at, null,
                    BranchHeadSha: candidate, MainHeadSha: basis, AcceptanceOutcome: "passed") : null));
        });
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            goalDetail: new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary));
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized); // Only this fixture lifecycle publishes inline on the loader thread.
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        using var finishLayout = new ManualResetEventSlim();
        var layoutWriting = Signal();
        var layoutThread = 0;
        var held = 0;
        Task layout = Task.CompletedTask;
        try
        {
            view.Render(new(new(false, 1, 0, 0, null, 0), [],
                [new("preview", "Epic", "Preview completion", "Active", "Developer", null, goal.Id.Value)], []));
            await Wait(queries.Entered.Task, "selected goal query entered");
            var completion = view.PreviewLoad;
            view.Window.Layout(new Size(30, 40));
            var label = Assert.Single(view.Window.SubViews.OfType<Label>(),
                item => item.Text.Contains("Verified: loading", StringComparison.Ordinal));
            label.TextChanging += (_, _) =>
            {
                if (Environment.CurrentManagedThreadId != Volatile.Read(ref layoutThread) ||
                    Interlocked.Exchange(ref held, 1) != 0) return;
                Assert.Equal(98, label.Viewport.Width);
                Assert.Contains("loading", label.Text);
                layoutWriting.SetResult();
                if (!finishLayout.Wait(TestHangGuard.Bound, Token))
                    throw new TimeoutException("Hang guard: release of layout's loading text write was not signalled.");
            };
            layout = Task.Run(() =>
            {
                Volatile.Write(ref layoutThread, Environment.CurrentManagedThreadId);
                view.Window.Layout(new Size(100, 40));
            });
            await Wait(layoutWriting.Task, "100-column layout reached loading text write");
            Assert.False(completion.IsCompleted);
            if (completeLayoutBeforeLoad)
            {
                finishLayout.Set();
                await Wait(layout, "initial layout finished before selected publication");
            }
            queries.Release.TrySetResult();
            await Wait(completion, "selected preview publication acknowledged");
            Assert.Contains("Verified: " + expected, view.PreviewText);
            if (!completeLayoutBeforeLoad)
            {
                // PreviewLoad really completed publication; the independent older layout is still held.
                Assert.False(layout.IsCompleted);
                finishLayout.Set();
                await Wait(layout, "older loading text write finished after publication");
            }
            Assert.Equal(goal.Id.Value, view.SelectedGoalId);
            var text = view.PreviewText;
            await view.HandleKeyAsync(Key.Tab);
            await view.HandleKeyAsync(Key.Enter);
            Assert.Contains("Verified: " + expected, Assert.Single(dialogs.Texts));
            Assert.Equal(2, queries.Reads);
            return text;
        }
        finally
        {
            finishLayout.Set();
            queries.Release.TrySetResult();
            await Wait(layout, "controlled layout cleanup");
            await Wait(view.PreviewLoad, "controlled preview cleanup");
        }
    }

    [Fact]
    public async Task QueuedPublication_BeforeApply_KeepsCompletionPending()
    {
        var entered = Signal();
        var result = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = new Posts();
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        using var session = new OwnerConsoleSelectedGoalSummary((_, _) =>
        { entered.SetResult(); return result.Task; }, posts.Post, applied.Add, Token);
        session.Select("selected");
        var completion = session.LastLoad;
        await Wait(entered.Task, "summary load entered");
        result.SetResult(Summary("selected"));
        var publish = await posts.Take();
        Assert.False(completion.IsCompleted);
        Assert.DoesNotContain(applied, item => item.Summary is not null);
        publish();
        await Wait(completion, "applied summary completion");
        Assert.Equal("selected", Assert.Single(applied.Where(item => item.Summary is not null)).Summary!.GoalId);
    }

    [Fact]
    public async Task QueuedPublication_SelectionChanges_RejectsObsoleteCallback()
    {
        var posts = new Posts();
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        using var session = new OwnerConsoleSelectedGoalSummary((id, _) =>
            Task.FromResult<OwnerGoalChangeSummary?>(Summary(id)), posts.Post, applied.Add, Token);
        session.Select("first");
        var completion = session.LastLoad;
        var obsolete = await posts.Take();
        session.Select("second");
        var current = await posts.Take();
        Assert.Same(completion, session.LastLoad);
        obsolete();
        Assert.DoesNotContain(applied, item => item.Summary is not null);
        Assert.False(completion.IsCompleted);
        current();
        await Wait(completion, "new selection publication");
        Assert.Equal("second", Assert.Single(applied.Where(item => item.Summary is not null)).Summary!.GoalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedPublication_CancelOrDispose_SettlesWithoutLateApply(bool dispose)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var posts = new Posts();
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        using var session = new OwnerConsoleSelectedGoalSummary((id, _) =>
            Task.FromResult<OwnerGoalChangeSummary?>(Summary(id)), posts.Post, applied.Add, lifetime.Token);
        session.Select("selected");
        var completion = session.LastLoad;
        var obsolete = await posts.Take();
        if (dispose) session.Dispose();
        else lifetime.Cancel();
        await Wait(completion, "abandoned queued publication completion");
        obsolete();
        Assert.DoesNotContain(applied, item => item.Summary is not null);
    }

    [Fact]
    public async Task Refresh_WhileLoading_CoalescesThroughAcknowledgedPublications()
    {
        var loads = Channel.CreateUnbounded<TaskCompletionSource<OwnerGoalChangeSummary?>>();
        var posts = new Posts();
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        var reads = 0;
        using var session = new OwnerConsoleSelectedGoalSummary((_, _) =>
        {
            Interlocked.Increment(ref reads);
            var load = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(loads.Writer.TryWrite(load));
            return load.Task;
        }, posts.Post, applied.Add, Token);
        session.Select("selected");
        var first = await TestHangGuard.WaitAsync(loads.Reader.ReadAsync(Token).AsTask(), "first refresh load entered");
        var completion = session.LastLoad;
        session.Select("selected", refresh: true);
        session.Select("selected", refresh: true);
        Assert.Same(completion, session.LastLoad);
        first.SetResult(Summary("selected"));
        var stale = await posts.Take();
        Assert.False(completion.IsCompleted);
        stale();
        var next = await TestHangGuard.WaitAsync(loads.Reader.ReadAsync(Token).AsTask(), "coalesced refresh load entered");
        Assert.StartsWith("[stale", applied.Last().Summary!.Verified.State);
        next.SetResult(Summary("selected"));
        var current = await posts.Take();
        Assert.False(completion.IsCompleted);
        current();
        await Wait(completion, "coalesced refresh publication");
        Assert.Equal(2, reads);
        Assert.Equal("pending", applied.Last().Summary!.Verified.State);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task, string name) => TestHangGuard.WaitAsync(task, name);
    private static OwnerGoalChangeSummary Summary(string id) => new(id, 1, "criteria", null,
        new("pending", [], "brief"), new("unavailable", [], "Git"), new("unavailable", [], "worker"),
        new("pending", [], "receipts"), "landing not recorded");

    private sealed class Posts
    {
        private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>();
        internal void Post(Action action) => Assert.True(_queue.Writer.TryWrite(action));
        internal Task<Action> Take() => TestHangGuard.WaitAsync(_queue.Reader.ReadAsync(Token).AsTask(), "UI publication queued");
    }

    private sealed class ControlledQueries(GoalSnapshot snapshot) : IOrchestratorStateQueries
    {
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly TaskCompletionSource Release = Signal();
        internal int Reads;
        public async Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(new GoalId(snapshot.Id), Assert.Single(ids));
            Interlocked.Increment(ref Reads);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return AgentOrchestratorKernel.FromSnapshot(new([snapshot], []));
        }
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Only the selected goal may be loaded.");
    }

    private sealed class EmptyTail : IGoalEventTail
    { public IReadOnlyList<string> ReadLast(string id, int count) => []; }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("No writes");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("No writes");
    }
}
