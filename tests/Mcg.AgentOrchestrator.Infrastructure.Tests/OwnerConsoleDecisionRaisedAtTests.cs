using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake queries own their snapshots; each read-model test owns its directory.
public sealed class OwnerConsoleDecisionRaisedAtTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BuilderCarriesOptionalRaiseTimesWithoutChangingQuestionEquality()
    {
        var harness = new OwnerConsoleHarness();
        var first = new OwnerQuestion("first", "goal", OwnerQuestionKind.HumanInput, "Ship?", "repo", "high", "yes");
        var second = new OwnerQuestion("second", "goal", OwnerQuestionKind.Clarification, "Which branch?");
        var source = new Questions(new([first, second], [], new Dictionary<string, DateTimeOffset> { [first.ItemId] = Now }));
        var builder = new OwnerConsoleViewModelBuilder(harness.State, source, harness.Liveness, new Epics(), harness.Clock);
        var (decisions, hidden) = await builder.ReadDecisionsAsync(CancellationToken.None);
        Assert.Equal(0, hidden);
        Assert.Equal(2, decisions.Length);
        Assert.Equal(Now, decisions.Single(item => item.Id == "first").RaisedAt);
        Assert.Null(decisions.Single(item => item.Id == "second").RaisedAt);
        Assert.Equal(first, decisions.Single(item => item.Id == "first").ToQuestion());
        Assert.Equal(second, decisions.Single(item => item.Id == "second").ToQuestion());
    }

    [Fact]
    public async Task ReadModelCarriesHumanRequestCreationTimeForLiveCandidatesOnly()
    {
        const string active = "11111111111111111111111111111111";
        const string terminal = "22222222222222222222222222222222";
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(active), "Active", [new TaskSpec(new TaskId("active-task"), "Work", AgentRole.Developer)]);
        kernel.CreateGoal(new GoalId(terminal), "Completed", [new TaskSpec(new TaskId("terminal-task"), "Work", AgentRole.Developer)]);
        var snapshot = kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals.Select(goal => goal with
                { Status = goal.Id == active ? GoalStatus.Active : GoalStatus.Completed }).ToArray(),
            HumanInputRequests =
            [
                new("live", active, null, "Ship?", Now),
                new("duplicate", active, null, "Ship?", Now.AddMinutes(1)),
                new("hidden", terminal, null, "Already done?", Now)
            ]
        };
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var result = await new OwnerQuestionReadModel(new Queries(snapshot), root).ReadAsync(CancellationToken.None);
            Assert.Equal("live", Assert.Single(result.Live).ItemId);
            Assert.NotNull(result.RaisedAt);
            Assert.Equal(new Dictionary<string, DateTimeOffset> { ["live"] = Now }, result.RaisedAt);
            Assert.Equal(["duplicate", "hidden"], result.Hidden.Select(item => item.Question.ItemId).Order());
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private sealed class Questions(OwnerQuestionSnapshot snapshot) : IOwnerQuestionSource
    {
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken token) => Task.FromResult(snapshot.Live);
        public Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(snapshot);
    }
    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }
    private sealed class Queries(OrchestratorSnapshot snapshot) : IOrchestratorStateQueries
    {
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(snapshot.Goals.Select(goal =>
                new GoalSummary(goal.Id, goal.Status.ToString(), goal.Objective, Now.ToString("O"))).ToArray());
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default)
        {
            var selected = ids.Select(id => id.Value).ToHashSet();
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Where(goal => selected.Contains(goal.Id)).ToArray(),
                HumanInputRequests = snapshot.HumanInputRequests.Where(request => selected.Contains(request.GoalId)).ToArray()
            }));
        }
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>(snapshot.HumanInputRequests);
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);
    }
}
