using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its workspace, SQLite stores and conduct log.
public sealed class AuthorOwnerQuestionHoldReconcilerTests
{
    [Xunit.Theory]
    [Xunit.InlineData(OperatorAnswerTargetKind.Clarification)]
    [Xunit.InlineData(OperatorAnswerTargetKind.HumanInput)]
    public async Task Resolved_target_clears_hold_once_without_changing_target(
        OperatorAnswerTargetKind kind)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateActiveGoal();
        string targetId;
        HumanInputRequest? request = null;
        CollaborationItem? itemBefore = null;
        if (kind == OperatorAnswerTargetKind.Clarification)
        {
            targetId = (await fixture.RaiseItem(goal)).Id;
            fixture.RaiseHold(goal, kind, targetId);
            await fixture.ResolveItem(goal);
            itemBefore = Xunit.Assert.Single(await fixture.Collaboration.ListAsync(goal.Id.Value));
            Xunit.Assert.True(CollaborationItemLifecycle.IsTerminal(itemBefore.Status));
        }
        else
        {
            request = fixture.Kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id,
                "Which runtime?", HumanWaitKind.SpecClarification);
            targetId = request.Id.Value;
            fixture.RaiseHold(goal, kind, targetId);
            fixture.Kernel.ParkGoal(goal.Id, "Resolve owner question while parked");
            fixture.Kernel.UnparkGoal(goal.Id, "Resume work");
            Xunit.Assert.True(request.IsCompleted);
        }
        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
        var answersBefore = request?.AnswerHistory.ToArray();
        var hold = Xunit.Assert.IsType<GoalHoldState>(goal.CurrentHold);
        var changed = new HashSet<GoalId>();

        fixture.Reconcile(changed);

        Xunit.Assert.Null(goal.CurrentHold);
        Xunit.Assert.Equal(goal.Id, Xunit.Assert.Single(changed));
        var entry = Xunit.Assert.Single(fixture.ReconciledEvents());
        Xunit.Assert.Equal(goal.Id.Value, entry.GetProperty("goalId").GetString());
        Xunit.Assert.Equal($"item={kind}:{targetId} target=resolved " +
            $"hold_started={hold.StartedAt.ToString("O", CultureInfo.InvariantCulture)}",
            entry.GetProperty("detail").GetString());
        if (itemBefore is not null)
        {
            var itemAfter = Xunit.Assert.Single(await fixture.Collaboration.ListAsync(goal.Id.Value));
            Xunit.Assert.Equal(itemBefore.Status, itemAfter.Status);
            Xunit.Assert.Equal(itemBefore.Resolution, itemAfter.Resolution);
            Xunit.Assert.Equal(itemBefore.ResolvedAt, itemAfter.ResolvedAt);
        }
        else
        {
            Xunit.Assert.Equal(answersBefore!, request!.AnswerHistory.ToArray());
        }

        changed.Clear();
        fixture.Reconcile(changed);
        Xunit.Assert.Empty(changed);
        Xunit.Assert.Single(fixture.ReconciledEvents());
    }

    [Xunit.Theory]
    [Xunit.InlineData("open")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("parked")]
    [Xunit.InlineData("steward")]
    [Xunit.InlineData("unparseable")]
    [Xunit.InlineData("out-of-scope")]
    [Xunit.InlineData("terminal")]
    public async Task Ineligible_hold_is_preserved_without_events(string scenario)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateActiveGoal();
        var item = await fixture.RaiseItem(goal);
        fixture.RaiseHold(goal, OperatorAnswerTargetKind.Clarification,
            scenario == "missing" ? Guid.NewGuid().ToString("N") : item.Id);
        if (scenario != "open") await fixture.ResolveItem(goal);
        if (scenario == "parked") fixture.Kernel.ParkGoal(goal.Id, "Keep parked");
        if (scenario == "terminal") fixture.Kernel.CancelGoal(goal.Id, "Stop work");
        if (scenario is "steward" or "unparseable")
        {
            fixture.Kernel.ObserveGoalHold(goal.Id,
                scenario == "steward" ? "steward-owner-question" : "author-owner-question",
                scenario == "steward" ? goal.CurrentHold!.Blocker : "author-owner-question item=bad",
                fixture.Now, TimeSpan.MaxValue);
            Xunit.Assert.Null(AuthorOwnerQuestionHold.TryParse(goal.CurrentHold));
        }
        var onlyGoalId = scenario == "out-of-scope" ? fixture.CreateActiveGoal().Id.Value : null;
        var before = Xunit.Assert.IsType<GoalHoldState>(goal.CurrentHold);
        var logBefore = File.ReadAllText(fixture.Conduct.CurrentPath);
        var changed = new HashSet<GoalId>();

        fixture.Reconcile(changed, onlyGoalId);

        Xunit.Assert.Equal(before.Identity, goal.CurrentHold?.Identity);
        Xunit.Assert.Equal(before.StartedAt, goal.CurrentHold?.StartedAt);
        Xunit.Assert.Empty(changed);
        Xunit.Assert.Equal(logBefore, File.ReadAllText(fixture.Conduct.CurrentPath));
        Xunit.Assert.Empty(fixture.ReconciledEvents());
    }

    [Xunit.Fact]
    public async Task Disabled_host_reconciles_without_calling_model()
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateActiveGoal();
        var item = await fixture.RaiseItem(goal);
        fixture.RaiseHold(goal, OperatorAnswerTargetKind.Clarification, item.Id);
        await fixture.ResolveItem(goal);
        var model = new CountingModel();
        var host = fixture.CreateDisabledHost(model);
        try
        {
            var changed = host.ServiceTick(fixture.Kernel);

            Xunit.Assert.Equal(goal.Id, Xunit.Assert.Single(changed));
            Xunit.Assert.Null(goal.CurrentHold);
            Xunit.Assert.Empty(host.CurrentRounds);
            Xunit.Assert.Equal(0, model.Calls);
            Xunit.Assert.Single(fixture.ReconciledEvents());
        }
        finally { host.Stop(); }
    }

    private sealed class CountingModel : IConductorAuthorModelRound
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult("unexpected model call");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hold-reconciler-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly CollaborationItemStore Collaboration;
        internal readonly ConductEventLogWriter Conduct;
        internal readonly DateTimeOffset Now = new(2026, 10, 24, 18, 0, 0, TimeSpan.Zero);

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            Collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Conduct = new ConductEventLogWriter(_workspace.ConductEventsLogPath, utcNow: () => Now);
        }

        internal Goal CreateActiveGoal()
        {
            var goal = Kernel.CreateGoal("Choose runtime",
                [new TaskSpec(TaskId.New(), "Plan runtime", AgentRole.Planner)]);
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            return goal;
        }

        internal Task<CollaborationItem> RaiseItem(Goal goal) => Collaboration.RaiseAsync(
            CollaborationItemType.Clarification, goal.Id.Value, "Runtime",
            "Question: Which runtime?\nFork kind: runtime", $"spec-clarification:{goal.Id.Value}:runtime");

        internal async Task ResolveItem(Goal goal) => Xunit.Assert.Equal(1,
            await Collaboration.ResolveOpenForGoalRaisedAtOrBeforeAsync(
                goal.Id.Value, "Owner question resolved", DateTimeOffset.UtcNow));

        internal void RaiseHold(Goal goal, OperatorAnswerTargetKind kind, string targetId) =>
            new ConductorAuthorOwnerQuestion(goal.Id.Value, kind, targetId, "Which runtime?",
                "Use Windows", "model-ask-owner").Raise(Kernel, goal,
                new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                Conduct, Now, new HashSet<GoalId>());

        internal void Reconcile(ISet<GoalId> changed, string? onlyGoalId = null) =>
            AuthorOwnerQuestionHoldReconciler.Reconcile(Kernel, Collaboration, Conduct, onlyGoalId, changed);

        internal JsonElement[] ReconciledEvents() => File.ReadAllLines(Conduct.CurrentPath)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).Where(entry => entry.GetProperty("eventKind").GetString() == "owner-question-hold-reconciled")
            .ToArray();

        internal ConductorAuthorHost CreateDisabledHost(IConductorAuthorModelRound model) => new(
            new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db")),
            Collaboration, model,
            SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory),
            new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath), _ => _root,
            new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory), Conduct,
            utcNow: () => Now, enabled: false);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
