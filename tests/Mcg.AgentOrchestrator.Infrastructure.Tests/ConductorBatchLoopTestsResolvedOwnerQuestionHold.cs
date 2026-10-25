using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsResolvedOwnerQuestionHold : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsResolvedOwnerQuestionHold(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public Task Resolved_unparked_goal_is_persisted_then_dispatched() =>
        RunScenario(resolve: true, unpark: true);

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public Task Open_unparked_goal_stays_held_and_visible_in_attention() =>
        RunScenario(resolve: false, unpark: true);

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public Task Resolved_parked_goal_stays_held_without_dispatch() =>
        RunScenario(resolve: true, unpark: false);

    private async Task RunScenario(bool resolve, bool unpark)
    {
        using var fixture = new AuthorFixture();
        var goal = fixture.CreateActiveGoal("Resume owner question");
        var planner = Xunit.Assert.Single(goal.Tasks);
        Xunit.Assert.Equal(AgentRole.Planner, planner.RequiredRole);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, planner.Status);
        var item = await fixture.Collaboration.RaiseAsync(CollaborationItemType.Clarification,
            goal.Id.Value, "Runtime", "Question: Which runtime?\nFork kind: runtime",
            $"spec-clarification:{goal.Id.Value}:runtime");
        Xunit.Assert.False(CollaborationItemLifecycle.IsTerminal(item.Status));
        // Store creation uses the real clock; start the injected clock after raised_at.
        fixture.Now = DateTimeOffset.UtcNow;
        // This independent goal keeps actual walk ticks running while the subject is excluded.
        fixture.CreateActiveGoal("Keep tick walk observable");
        fixture.Host.ServiceTick(fixture.Kernel);
        await fixture.Model.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Xunit.Assert.Null(goal.CurrentHold);
        fixture.Model.Reply.SetResult("""
            {"kind":"ask-owner","question":"Which runtime?","recommendation":"Use Windows"}
            """);
        // Await completion, but let the real loop harvest and raise the hold on tick 1.
        await Task.WhenAll(fixture.Host.CurrentRounds).WaitAsync(TimeSpan.FromSeconds(30));

        var currentTick = 1;
        var ticks = new List<int>();
        var dispatches = new List<(int Tick, string? HoldState)>();
        var persisted = new List<(int Tick, GoalHoldState? Hold)>();
        GoalHoldState? originalHold = null;
        var resumed = resolve && unpark;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: candidate =>
            {
                if (candidate.Id == goal.Id)
                    dispatches.Add((currentTick, candidate.CurrentHold?.State));
                return DispatchStartOutcome.Deferred("Fixture records dispatch without starting a worker");
            },
            utcNow: () => fixture.Now);

        var summary = new ConductorBatchLoop(utcNow: () => fixture.Now)
            .WithAuthor(fixture.Host).Run(fixture.Kernel, driver,
                ConductorAutonomyPolicy.Conservative, Path.Combine(fixture.Root, "stop.signal"),
                maxIterations: 6, watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false, keepAliveWhenIdle: true,
                persistGoalTick: (_, changed) =>
                {
                    if (changed.Contains(goal.Id)) persisted.Add((currentTick, goal.CurrentHold));
                },
                onTick: tick =>
                {
                    ticks.Add(tick.Tick);
                    if (tick.Tick <= 2)
                    {
                        var hold = Xunit.Assert.IsType<GoalHoldState>(goal.CurrentHold);
                        Xunit.Assert.Equal("author-owner-question", hold.State);
                        Xunit.Assert.Contains($"item=Clarification:{item.Id}", hold.Blocker);
                        originalHold ??= hold;
                        Xunit.Assert.Equal(originalHold.Identity, hold.Identity);
                        Xunit.Assert.Empty(dispatches);
                    }
                    if (tick.Tick == 2)
                    {
                        fixture.Kernel.ParkGoal(goal.Id, "Owner parks pending question");
                        if (resolve)
                            Xunit.Assert.Equal(1, fixture.Collaboration
                                .ResolveOpenForGoalRaisedAtOrBeforeAsync(goal.Id.Value,
                                    "Goal parked: Owner parks pending question", fixture.Now)
                                .GetAwaiter().GetResult());
                        if (unpark) fixture.Kernel.UnparkGoal(goal.Id, "Resume planner");
                        Xunit.Assert.Equal(unpark ? GoalStatus.Active : GoalStatus.Parked, goal.Status);
                        Xunit.Assert.Equal(originalHold!.Identity, goal.CurrentHold?.Identity);
                    }
                    if (tick.Tick == 3 && resumed)
                    {
                        Xunit.Assert.Null(goal.CurrentHold);
                        Xunit.Assert.Contains(persisted, save => save.Tick == 3 && save.Hold is null);
                        Xunit.Assert.Empty(dispatches);
                        Xunit.Assert.Single(fixture.ReconciledEvents());
                    }
                    if (tick.Tick >= 3 && !resumed)
                    {
                        Xunit.Assert.Equal(originalHold!.Identity, goal.CurrentHold?.Identity);
                        Xunit.Assert.Equal(originalHold.StartedAt, goal.CurrentHold?.StartedAt);
                        Xunit.Assert.Empty(dispatches);
                    }
                    fixture.Now += TimeSpan.FromSeconds(1);
                    currentTick = tick.Tick + 1;
                });

        Xunit.Assert.Equal(6, summary.Ticks);
        Xunit.Assert.Equal(Enumerable.Range(1, 6), ticks);
        Xunit.Assert.NotNull(originalHold);
        Xunit.Assert.Equal(1, fixture.Model.Calls);
        if (resumed)
        {
            Xunit.Assert.NotEmpty(dispatches);
            Xunit.Assert.InRange(dispatches[0].Tick, 4, 5);
            Xunit.Assert.All(dispatches, dispatch =>
                Xunit.Assert.NotEqual("author-owner-question", dispatch.HoldState));
            var entry = Xunit.Assert.Single(fixture.ReconciledEvents());
            Xunit.Assert.Equal(goal.Id.Value, entry.GetProperty("goalId").GetString());
            Xunit.Assert.Contains($"item=Clarification:{item.Id} target=resolved",
                entry.GetProperty("detail").GetString());
        }
        else
        {
            Xunit.Assert.Empty(dispatches);
            Xunit.Assert.Empty(fixture.ReconciledEvents());
            if (!resolve)
            {
                var attention = await fixture.Collaboration.GetAttentionQueueAsync();
                var visible = Xunit.Assert.Single(attention.Where(candidate => candidate.Id == item.Id));
                Xunit.Assert.False(CollaborationItemLifecycle.IsTerminal(visible.Status));
            }
        }
    }

    private sealed class OwnerModel : IConductorAuthorModelRound
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult(true);
            return Reply.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class AuthorFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"hold-resume-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly CollaborationItemStore Collaboration;
        internal readonly OwnerModel Model = new();
        internal readonly ConductorAuthorHost Host;
        internal DateTimeOffset Now;

        internal AuthorFixture()
        {
            Directory.CreateDirectory(Root);
            _workspace = OrchestratorWorkspace.ForDirectory(Root);
            Collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Host = new ConductorAuthorHost(
                new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db")),
                Collaboration, Model,
                SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory),
                new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath), _ => Root,
                new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(_workspace.ConductEventsLogPath, utcNow: () => Now),
                utcNow: () => Now);
        }

        internal Goal CreateActiveGoal(string objective)
        {
            var goal = Kernel.CreateGoal(objective,
                [new TaskSpec(TaskId.New(), "Plan runtime", AgentRole.Planner)]);
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            return goal;
        }

        internal JsonElement[] ReconciledEvents() => File.ReadAllLines(_workspace.ConductEventsLogPath)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).Where(entry => entry.GetProperty("eventKind").GetString() == "owner-question-hold-reconciled")
            .ToArray();

        public void Dispose()
        {
            Host.Stop();
            Directory.Delete(Root, recursive: true);
        }
    }
}
