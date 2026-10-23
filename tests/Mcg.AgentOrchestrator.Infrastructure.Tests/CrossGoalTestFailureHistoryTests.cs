using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its directory, in-memory goals, and clock.
public sealed class CrossGoalTestFailureHistoryTests
{
    private const string Key = "ExampleTests.Fails";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CombinesOnlyOtherGoalFailuresWithinWindowAtMethodLevel()
    {
        using var fixture = new Fixture();
        var censusGoal = GoalId.New();
        var ledgerGoal = GoalId.New();
        fixture.Census(fixture.Current, Key);
        fixture.Census(GoalId.New(), Key, inside: true);
        fixture.Census(GoalId.New(), Key, at: Now.AddDays(-15));
        fixture.Census(GoalId.New(), Key, at: Now.AddMinutes(1));
        fixture.Census(GoalId.New(), Key, kind: "apparatus-regate");
        fixture.Census(censusGoal, Key);
        fixture.Ledger(ledgerGoal, "Namespace.ExampleTests.Fails(value: 1)");
        fixture.Ledger(GoalId.New(), Key, at: Now.AddDays(-15));
        fixture.Marker("actionable-red", Key);
        fixture.Marker("red", Key, Now.AddDays(-15));
        var markerGoal = fixture.Marker("red", "ExampleTests.Fails(value: \"x,y\")");
        File.AppendAllLines(fixture.CensusPath, ["", "{malformed", "null", "{}"]);
        File.AppendAllLines(fixture.LedgerPath, ["", "{malformed", "null", "{}"]);

        var result = fixture.Read(["ExampleTests.Fails(value: \"digest\")", "NoHistoryTests.NewFailure"]);

        Assert.Equal(new[] { Key }, result.Keys);
        Assert.Equal(new[] { censusGoal.Value, ledgerGoal.Value, markerGoal.Value }.Order(StringComparer.Ordinal), result[Key]);
    }

    [Fact]
    public void OnlySameGoalRecordsGiveNoHistory()
    {
        const string regression = "RunGoalServiceProcessContractTests.RunGoalServiceRealAdvanceStepCompletesTwoEchoTasksSequentially";
        using var fixture = new Fixture();
        fixture.Census(fixture.Current, regression);
        fixture.Ledger(fixture.Current, regression, outcome: "confirmed-failure");
        Assert.Empty(fixture.Read([regression]));
    }

    [Theory]
    [InlineData("same-goal")]
    [InlineData("inside")]
    [InlineData("old")]
    [InlineData("actionable")]
    [InlineData("malformed")]
    public void ExcludedEvidenceCannotAuthorizeRerun(string control)
    {
        using var fixture = new Fixture();
        switch (control)
        {
            case "same-goal": fixture.Census(fixture.Current, Key); fixture.Ledger(fixture.Current, Key); break;
            case "inside": fixture.Census(GoalId.New(), Key, inside: true); break;
            case "old": fixture.Census(GoalId.New(), Key, at: Now.AddDays(-15)); break;
            case "actionable": fixture.Marker("actionable-red", Key); break;
            case "malformed": File.WriteAllLines(fixture.CensusPath, ["", "{malformed"]); break;
        }
        Assert.Empty(fixture.Read([Key]));
    }

    [Fact]
    public void MissingOrUnreadableStoresContributeNothing()
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Read([Key]));
        Directory.CreateDirectory(fixture.CensusPath);
        Directory.CreateDirectory(fixture.LedgerPath);
        Assert.Empty(fixture.Read([Key]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
        private readonly List<Goal> _goals = [];
        internal GoalId Current { get; } = new("21da18e8e64e4c9c913e81c86612d381");
        internal string CensusPath => Path.Combine(_root, "census.jsonl");
        internal string LedgerPath => Path.Combine(_root, ".orchestrator", "lane-flakes.jsonl");

        internal Fixture() => Directory.CreateDirectory(Path.Combine(_root, ".orchestrator"));

        internal void Census(GoalId goal, string identity, bool inside = false, DateTimeOffset? at = null, string kind = "gate-failure") =>
            File.AppendAllLines(CensusPath, [JsonSerializer.Serialize(new AcceptanceFailingTestIndexRecord(
                1, kind, goal.Value, at ?? Now, TestIdentity: identity, InsideChangedPaths: inside), JsonOptions)]);

        internal void Ledger(GoalId goal, string identity, string outcome = "flake", DateTimeOffset? at = null) =>
            new AcceptanceLaneFlakeLedger(_root).Append(new(goal.Value, "attempt", "Remainder", "tree", "first", [],
                [identity], "failed", "rerun", [], [], null, outcome, at ?? Now));

        internal GoalId Marker(string outcome, string identity, DateTimeOffset? at = null)
        {
            var clock = new FixedClock(at ?? Now);
            var kernel = new AgentOrchestratorKernel(clock);
            var tester = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
            var goal = kernel.CreateGoal("history", [tester]);
            kernel.RecordFindingEvidenceRun(goal.Id, tester.Id, PreTesterEvidenceIndexLines.FormatMarker(
                new(outcome, "candidate", "receipt", ["Infrastructure.Tests:ExampleTests"], [], null, [identity])));
            _goals.Add(goal);
            return goal.Id;
        }

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Read(IReadOnlyList<string> identities) =>
            new CrossGoalTestFailureHistory(CensusPath, _root, () => _goals).Read(Current, identities, Now);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
