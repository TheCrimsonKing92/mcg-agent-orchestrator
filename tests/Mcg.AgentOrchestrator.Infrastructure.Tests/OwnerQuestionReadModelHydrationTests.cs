using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its probe, snapshot and temporary directory.
public sealed class OwnerQuestionReadModelHydrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Active = "11111111111111111111111111111111";
    private const string Completed = "22222222222222222222222222222222";
    private const string Cancelled = "33333333333333333333333333333333";
    private const string Superseded = "44444444444444444444444444444444";
    private const string Missing = "55555555555555555555555555555555";
    private const string Parked = "66666666666666666666666666666666";
    private const string OtherActive = "77777777777777777777777777777777";

    [Fact]
    public async Task Refresh_SkipsTerminalAndMissingGoals_AndMatchesFullyHydratedMain()
    {
        var arrangement = Arrangement();
        var probe = new Probe(arrangement);
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var source = new OwnerQuestionReadModel(probe, root);
            var expected = FullyHydratedMain(AgentOrchestratorKernel.FromSnapshot(arrangement));

            var actual = await source.ReadAsync(CancellationToken.None);
            var refreshed = await source.ReadAsync(CancellationToken.None);

            Assert.Equal(expected.Live, actual.Live);
            Assert.Equal(expected.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason),
                actual.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason));
            Assert.Equal(actual.Live, refreshed.Live);
            Assert.Equal(actual.Hidden, refreshed.Hidden);
            Assert.Equal(2, probe.Loaded.Count);
            Assert.All(probe.Loaded, ids => Assert.Equal([Active, Parked, OtherActive], ids));
            Assert.DoesNotContain(probe.Loaded.SelectMany(ids => ids), id =>
                id is Completed or Cancelled or Superseded or Missing);
            Assert.Equal(1, probe.HoldQueries);
            Assert.Equal([Completed, Cancelled, Superseded], Assert.Single(probe.HoldQueryIds));
            Assert.Equal(["same-earliest", "different-case", "other-goal", "active-hold"],
                actual.Live.Select(question => question.ItemId));
            Assert.Equal(new Dictionary<string, string>
            {
                ["completed-request"] = "goal-completed", ["cancelled-request"] = "goal-cancelled",
                ["superseded-request"] = "goal-superseded", ["missing-request"] = "goal-missing",
                ["future-evidence"] = "non-blocking-kind", ["same-tie"] = "duplicate",
                ["same-later"] = "duplicate", ["terminal-hold"] = "goal-completed"
            }, actual.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason));
            Assert.DoesNotContain(actual.Live.Concat(actual.Hidden.Select(item => item.Question)),
                question => question.ItemId == "parked-request");
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task MissingActiveRequest_UsesKernelRepair_AndMatchesMain()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal(Active, "Repair wait", AgentRole.Developer);
        harness.Kernel.RequestHumanInput(goal.Id, goal.Tasks.Single().Id, "Should work continue?");
        var snapshot = harness.Kernel.ExportSnapshot() with { HumanInputRequests = [] };
        var probe = new Probe(snapshot);
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var expected = FullyHydratedMain(AgentOrchestratorKernel.FromSnapshot(snapshot));
            var actual = await new OwnerQuestionReadModel(probe, root).ReadAsync(CancellationToken.None);

            Assert.Equal(expected.Live, actual.Live);
            Assert.Equal("Should work continue?", Assert.Single(actual.Live).Text);
            Assert.Empty(actual.Hidden);
            Assert.Equal([Active], Assert.Single(probe.Loaded));
            Assert.Equal(0, probe.HoldQueries);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task GoalBecomesTerminal_ExtendsSessionCache_WithoutLoadingItAgain()
    {
        var probe = new Probe(Arrangement());
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var source = new OwnerQuestionReadModel(probe, root);
            await source.ReadAsync(CancellationToken.None);
            probe.Snapshot = probe.Snapshot with
            {
                Goals = probe.Snapshot.Goals.Select(goal => goal.Id == Active
                    ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
            };

            var actual = await source.ReadAsync(CancellationToken.None);
            var refreshed = await source.ReadAsync(CancellationToken.None);
            var expected = FullyHydratedMain(AgentOrchestratorKernel.FromSnapshot(probe.Snapshot));

            Assert.Equal(expected.Live, actual.Live);
            Assert.Equal(expected.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason),
                actual.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason));
            Assert.All(probe.Loaded.Skip(1), ids => Assert.Equal([Parked, OtherActive], ids));
            Assert.Equal(2, probe.HoldQueries);
            Assert.Equal([Active], probe.HoldQueryIds[1]);
            Assert.Equal(actual.Hidden, refreshed.Hidden);
            Assert.Contains(actual.Hidden, item => item.Question.ItemId == "active-hold" &&
                item.Reason == "goal-completed");
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task OnlyTerminalGoals_NoHydration_AndHoldQueryIsCached()
    {
        var snapshot = Arrangement();
        var probe = new Probe(snapshot with { Goals = snapshot.Goals.Where(goal => goal.Id == Completed).ToArray() });
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var source = new OwnerQuestionReadModel(probe, root);
            var first = await source.ReadAsync(CancellationToken.None);
            var second = await source.ReadAsync(CancellationToken.None);
            Assert.Empty(probe.Loaded);
            Assert.Empty(second.Live);
            Assert.Equal(first.Hidden, second.Hidden);
            Assert.Equal(1, probe.HoldQueries);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task CachedTerminalGoal_CompletedAndNewRequests_MatchCurrentMain()
    {
        var probe = new Probe(Arrangement());
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var source = new OwnerQuestionReadModel(probe, root);
            var first = await source.ReadAsync(CancellationToken.None);
            Assert.Contains(first.Hidden, item => item.Question.ItemId == "completed-request");
            probe.Snapshot = probe.Snapshot with
            {
                HumanInputRequests = probe.Snapshot.HumanInputRequests.Select(request =>
                    request.Id == "completed-request"
                        ? request with { IsCompleted = true, Answer = "Resolved", AnsweredAt = Now }
                        : request)
                    .Append(Request("new-terminal-request", Completed, "New terminal wait")).ToArray()
            };

            var actual = await source.ReadAsync(CancellationToken.None);
            var expected = FullyHydratedMain(AgentOrchestratorKernel.FromSnapshot(probe.Snapshot));

            Assert.Equal(expected.Live, actual.Live);
            Assert.Equal(expected.Hidden, actual.Hidden);
            Assert.DoesNotContain(actual.Hidden, item => item.Question.ItemId == "completed-request");
            Assert.Contains(actual.Hidden, item => item.Question.ItemId == "new-terminal-request" &&
                item.Reason == "goal-completed");
            Assert.Equal(1, probe.HoldQueries);
            Assert.DoesNotContain(probe.Loaded.SelectMany(ids => ids), id => id == Completed);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task TerminalGoalReactivatedThenCancelled_DiscardsOldHoldAndQueriesOnlyThatGoal()
    {
        var probe = new Probe(Arrangement());
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var source = new OwnerQuestionReadModel(probe, root);
            await source.ReadAsync(CancellationToken.None);
            probe.Snapshot = probe.Snapshot with
            {
                Goals = probe.Snapshot.Goals.Select(goal => goal.Id == Completed
                    ? goal with { Status = GoalStatus.Active, CurrentHold = null } : goal).ToArray()
            };
            var active = await source.ReadAsync(CancellationToken.None);
            Assert.DoesNotContain(active.Hidden, item => item.Question.ItemId == "terminal-hold");
            Assert.Equal(1, probe.HoldQueries);

            probe.Snapshot = probe.Snapshot with
            {
                Goals = probe.Snapshot.Goals.Select(goal => goal.Id == Completed
                    ? goal with { Status = GoalStatus.Cancelled } : goal).ToArray()
            };
            var actual = await source.ReadAsync(CancellationToken.None);
            var expected = FullyHydratedMain(AgentOrchestratorKernel.FromSnapshot(probe.Snapshot));
            Assert.Equal(expected.Live, actual.Live);
            Assert.Equal(expected.Hidden, actual.Hidden);
            Assert.Equal(2, probe.HoldQueries);
            Assert.Equal([Completed], probe.HoldQueryIds[1]);
            Assert.DoesNotContain(probe.Loaded.Last(), id => id == Completed);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task ChangedTerminalHold_RefreshesOnlyChangedGoal_WithoutHydration()
    {
        var probe = new Probe(Arrangement());
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var source = new OwnerQuestionReadModel(probe, root);
            await source.ReadAsync(CancellationToken.None);
            probe.Snapshot = probe.Snapshot with
            {
                Goals = probe.Snapshot.Goals.Select(goal => goal.Id == Completed
                    ? goal with { CurrentHold = goal.CurrentHold! with
                    {
                        Identity = "replacement-hold", Blocker = "question=Updated hold evidence=[receipt]",
                        StartedAt = Now.AddMinutes(1)
                    } } : goal).ToArray()
            };
            probe.UpdatedAt[Completed] = Now.AddMinutes(1).ToString("O");

            var actual = await source.ReadAsync(CancellationToken.None);
            var refreshed = await source.ReadAsync(CancellationToken.None);

            Assert.DoesNotContain(actual.Hidden, item => item.Question.ItemId == "terminal-hold");
            var replacement = Assert.Single(actual.Hidden, item => item.Question.ItemId == "replacement-hold");
            Assert.Equal("Updated hold", replacement.Question.Text);
            Assert.Equal("goal-completed", replacement.Reason);
            Assert.Equal(actual.Live, refreshed.Live);
            Assert.Equal(actual.Hidden, refreshed.Hidden);
            Assert.Equal(2, probe.HoldQueries);
            Assert.Equal([Completed], probe.HoldQueryIds[1]);
            Assert.DoesNotContain(probe.Loaded.SelectMany(ids => ids), id =>
                id is Completed or Cancelled or Superseded);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedTerminalHold_DropsCachedQuestion_AndMatchesMain(bool otherHold)
    {
        var probe = new Probe(Arrangement());
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var source = new OwnerQuestionReadModel(probe, root);
            var first = await source.ReadAsync(CancellationToken.None);
            Assert.Contains(first.Hidden, item => item.Question.ItemId == "terminal-hold");
            probe.Snapshot = probe.Snapshot with
            {
                Goals = probe.Snapshot.Goals.Select(goal => goal.Id == Completed
                    ? goal with { CurrentHold = otherHold
                        ? goal.CurrentHold! with { State = "acceptance" } : null } : goal).ToArray()
            };
            probe.UpdatedAt[Completed] = Now.AddMinutes(1).ToString("O");

            var actual = await source.ReadAsync(CancellationToken.None);
            var expected = FullyHydratedMain(AgentOrchestratorKernel.FromSnapshot(probe.Snapshot));

            Assert.Equal(expected.Live, actual.Live);
            Assert.Equal(expected.Hidden, actual.Hidden);
            Assert.DoesNotContain(actual.Hidden, item => item.Question.ItemId == "terminal-hold");
            Assert.Equal(2, probe.HoldQueries);
            Assert.Equal([Completed], probe.HoldQueryIds[1]);
            Assert.DoesNotContain(probe.Loaded.SelectMany(ids => ids), id => id == Completed);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static OrchestratorSnapshot Arrangement() => new(
        [Goal(Active, GoalStatus.Active, "active-hold"), Goal(Completed, GoalStatus.Completed, "terminal-hold"),
         Goal(Cancelled, GoalStatus.Cancelled), Goal(Superseded, GoalStatus.Superseded),
         Goal(Parked, GoalStatus.Parked), Goal(OtherActive, GoalStatus.Active)],
        [Request("same-later", Active, "Same") with { RequestedAt = Now.AddMinutes(2) },
         Request("completed-request", Completed, "Completed"), Request("cancelled-request", Cancelled, "Cancelled"),
         Request("same-earliest", Active, "Same") with { RequestedAt = Now.AddMinutes(1), SuggestedDefaultAnswer = "yes" },
         Request("superseded-request", Superseded, "Superseded"), Request("missing-request", Missing, "Missing"),
         Request("same-tie", Active, "Same") with { RequestedAt = Now.AddMinutes(1) },
         Request("different-case", Active, "same"), Request("other-goal", OtherActive, "Same"),
         Request("future-evidence", Active, "Later evidence") with { Kind = HumanWaitKind.ProspectiveAcceptanceEvidence },
         Request("parked-request", Parked, "Parked wait")]);

    private static GoalSnapshot Goal(string id, GoalStatus status, string? hold = null)
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(id), id,
            [new TaskSpec(new TaskId(id + "-task"), "Work", AgentRole.Developer)]);
        return kernel.ExportSnapshot().Goals.Single() with
        {
            Status = status,
            CurrentHold = hold is null ? null :
                new GoalHoldSnapshot(hold, "steward-owner-question", "question=Review hold evidence=[receipt]", Now)
        };
    }

    private static HumanInputRequestSnapshot Request(string id, string goal, string text) =>
        new(id, goal, null, text, Now);

    // Frozen main algorithm over a fully hydrated kernel. This oracle never calls the changed read model.
    private static OwnerQuestionSnapshot FullyHydratedMain(AgentOrchestratorKernel kernel)
    {
        var candidates = kernel.HumanInputRequests.Where(request => !request.IsCompleted).Select(request =>
            (Question: new OwnerQuestion(request.Id.Value, request.GoalId.Value, OwnerQuestionKind.HumanInput,
                request.Question, ProposedDefault: request.SuggestedDefaultAnswer), At: request.RequestedAt,
                Blocking: HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind))).ToList();
        foreach (var goal in kernel.Goals)
            if (goal.CurrentHold is { } hold && hold.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase))
                candidates.Add((new OwnerQuestion(hold.Identity, goal.Id.Value, OwnerQuestionKind.StewardHold,
                    "Review hold"), hold.StartedAt, true));
        var goals = kernel.Goals.ToDictionary(goal => goal.Id.Value, goal => goal.Status, StringComparer.OrdinalIgnoreCase);
        var hidden = new List<HiddenOwnerQuestion>();
        var eligible = candidates.Where(candidate =>
        {
            var reason = !goals.TryGetValue(candidate.Question.GoalId, out var status) ? "goal-missing" : status switch
            {
                GoalStatus.Completed => "goal-completed", GoalStatus.Cancelled => "goal-cancelled",
                GoalStatus.Superseded => "goal-superseded", _ => candidate.Blocking ? null : "non-blocking-kind"
            };
            if (reason is null) return true;
            hidden.Add(new HiddenOwnerQuestion(candidate.Question, reason));
            return false;
        }).ToArray();
        var seen = new HashSet<(string, string)>();
        var live = new HashSet<OwnerQuestion>();
        foreach (var candidate in eligible.OrderBy(candidate => candidate.At))
            if (seen.Add((candidate.Question.GoalId.ToUpperInvariant(), candidate.Question.Text))) live.Add(candidate.Question);
            else hidden.Add(new HiddenOwnerQuestion(candidate.Question, "duplicate"));
        return new OwnerQuestionSnapshot(eligible.Select(candidate => candidate.Question).Where(live.Contains).ToArray(), hidden);
    }

    private sealed class Probe(OrchestratorSnapshot snapshot) : IOrchestratorStateQueries
    {
        internal OrchestratorSnapshot Snapshot = snapshot;
        internal readonly List<string[]> Loaded = [];
        internal int HoldQueries;
        internal readonly List<string[]> HoldQueryIds = [];
        internal readonly Dictionary<string, string> UpdatedAt = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(Snapshot.Goals.Select(goal =>
                new GoalSummary(goal.Id, goal.Status.ToString(), goal.Objective,
                    UpdatedAt.GetValueOrDefault(goal.Id, Now.ToString("O")))).ToArray());
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default)
        {
            Loaded.Add(ids.Select(id => id.Value).ToArray());
            var selected = ids.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(Snapshot with
            {
                Goals = Snapshot.Goals.Where(goal => selected.Contains(goal.Id)).ToArray(),
                HumanInputRequests = Snapshot.HumanInputRequests.Where(request => selected.Contains(request.GoalId)).ToArray()
            }));
        }
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>(Snapshot.HumanInputRequests.Where(request => !request.IsCompleted).ToArray());
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            HoldQueries++;
            HoldQueryIds.Add(goalIds.Select(id => id.Value).ToArray());
            var selected = goalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>(Snapshot.Goals.Where(goal =>
                selected.Contains(goal.Id) &&
                goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded &&
                goal.CurrentHold?.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase) == true)
                .Select(goal => new TerminalOwnerQuestionHold(goal.Id, goal.CurrentHold!.Identity,
                    goal.CurrentHold.State, goal.CurrentHold.Blocker, goal.CurrentHold.StartedAt)).ToArray());
        }
    }
}
