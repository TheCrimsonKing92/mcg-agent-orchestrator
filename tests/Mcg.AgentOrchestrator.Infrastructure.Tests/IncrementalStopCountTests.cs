using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all goals, membership events and clocks are local to each case.
public sealed class IncrementalStopCountTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
    private static string BaselineId => new('a', 32);
    private static string ComparisonId => new('b', 32);

    [Fact]
    public void Evaluate_UnlandedPairWithUnrelatedTerminals_DoesNotMeetStopRule()
    {
        var record = WithPair(Record(), BaselineId, ComparisonId);
        var reading = Evaluate(record, Goals(GoalStatus.Active));

        Assert.Equal(0, reading.ObservedCount);
        Assert.False(reading.StopRuleMet);
        Assert.Equal("landed-member-goals", reading.ObservedBasis);
        Assert.Equal(12, reading.StopTarget);
    }

    [Fact]
    public void Evaluate_LandedPairs_CountsOnlyCompletedMembersAgainstTarget()
    {
        var record = WithPair(Record(), BaselineId, ComparisonId);
        var snapshots = Snapshots(GoalStatus.Completed);
        var goals = Hydrate(snapshots);
        var reading = Evaluate(record, goals);
        Assert.Equal(2, reading.ObservedCount);
        Assert.False(reading.StopRuleMet);
        Assert.Equal(12, reading.StopTarget);
        Assert.Equal("landed-member-goals", reading.ObservedBasis);

        var smaller = record with { Spec = record.Spec with { StopRule = new(2, ExperimentStopUnit.Goals) } };
        reading = Evaluate(smaller, goals);
        Assert.Equal(2, reading.ObservedCount);
        Assert.True(reading.StopRuleMet);
        Assert.Equal(2, reading.StopTarget);
        Assert.Equal("landed-member-goals", reading.ObservedBasis);

        var extended = WithPair(record, new('c', 32), new('d', 32));
        goals = Hydrate([.. snapshots, Snapshot(new('c', 32), GoalStatus.Completed),
            Snapshot(new('d', 32), GoalStatus.Failed)]);
        reading = Evaluate(extended, goals);
        Assert.Equal(3, reading.ObservedCount);
        Assert.False(reading.StopRuleMet);
        Assert.Equal("landed-member-goals", reading.ObservedBasis);
    }

    [Theory]
    [InlineData("aaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", GoalStatus.Completed, 1)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", GoalStatus.Completed, 1)]
    [InlineData("missing", "missing", GoalStatus.Completed, 0)]
    [InlineData("aaaa", "aaaa", GoalStatus.Cancelled, 0)]
    [InlineData("aaaa", "aaaa", GoalStatus.Superseded, 0)]
    [InlineData("aaaa", "aaaa", GoalStatus.Failed, 0)]
    [InlineData(" AAAA ", "missing", GoalStatus.Completed, 1)]
    [InlineData(" ", "", GoalStatus.Completed, 0)]
    public void Evaluate_MemberReferences_CountsDistinctResolvedCompletedGoals(
        string baseline, string comparison, GoalStatus status, int expected)
    {
        var record = Record() with { Spec = Record().Spec with { Baseline = new(ExperimentBaselineKind.GoalCohort,
            BaselineGoalIds: [baseline], ComparisonGoalIds: [comparison], IncrementalMembers: true) } };
        var reading = Evaluate(record, Hydrate([Snapshot(BaselineId, status)]));
        Assert.Equal(expected, reading.ObservedCount);
        Assert.Equal("landed-member-goals", reading.ObservedBasis);
    }

    [Fact]
    public void Evaluate_AmbiguousPrefix_CountsZeroButExactMatchStillWins()
    {
        var record = WithPair(Record(), "aaaa", "missing");
        var goals = Hydrate([Snapshot(BaselineId, GoalStatus.Completed),
            Snapshot("aaaab" + new string('a', 27), GoalStatus.Completed)]);
        Assert.Equal(0, Evaluate(record, goals).ObservedCount);

        record = WithPair(Record(), BaselineId, "missing");
        Assert.Equal(1, Evaluate(record, goals).ObservedCount);
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.GoalCohort, null)]
    [InlineData(ExperimentBaselineKind.GoalCohort, false)]
    [InlineData(ExperimentBaselineKind.BeforeAfterWindow, true)]
    [InlineData(ExperimentBaselineKind.TwinGoal, true)]
    [InlineData(ExperimentBaselineKind.AlternatingGates, true)]
    public void Evaluate_OtherBaselines_PreservesGlobalStopProgress(
        ExperimentBaselineKind kind, bool? incremental)
    {
        var baseline = new ExperimentBaseline(kind, Since: At.AddDays(-1), Until: At,
            TwinGoalId: BaselineId, ComparisonGoalId: ComparisonId,
            BaselineGoalIds: [BaselineId], ComparisonGoalIds: [ComparisonId], IncrementalMembers: incremental);
        var record = Record() with { Spec = Record().Spec with { Baseline = baseline } };
        var reading = Evaluate(record, Goals(GoalStatus.Active));

        // The original global rule counts the 13 unrelated dispatches in [At, At + 2 days).
        Assert.Equal(13, reading.ObservedCount);
        Assert.True(reading.StopRuleMet);
        Assert.Equal(12, reading.StopTarget);
        Assert.Null(reading.ObservedBasis);
    }

    [Fact]
    public void Evaluate_IncrementalGatesUnit_PreservesSuppliedGateCount()
    {
        var record = WithPair(Record(), BaselineId, ComparisonId);
        record = record with { Spec = record.Spec with { StopRule = new(12, ExperimentStopUnit.Gates) } };
        var reading = ExperimentReading.Evaluate(record, Goals(GoalStatus.Completed),
            new Dictionary<string, DateTimeOffset>(), [], At.AddDays(2), 5, ExperimentHostLedgers.Empty);

        Assert.Equal(5, reading.ObservedCount);
        Assert.False(reading.StopRuleMet);
        Assert.Equal(12, reading.StopTarget);
        Assert.Null(reading.ObservedBasis);
    }

    internal static ExperimentSpec Spec() => new("Matched briefs reduce rounds per landing",
        new(ExperimentInterventionKind.BriefOrPromptChange, "Shorter brief"),
        new(ExperimentBaselineKind.GoalCohort, BaselineGoalIds: [], ComparisonGoalIds: [], IncrementalMembers: true),
        ["rounds-per-landing"], new("productive-rounds", new("productive-rounds", ">", 1000)),
        new(12, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<=", 0)], [new("rounds-per-landing", ">", 0)]));

    private static ExperimentRecord Record() => new("incremental", Spec(),
        At, ExperimentOutcomeState.Open, null);

    private static ExperimentRecord WithPair(ExperimentRecord record, string baseline, string comparison) =>
        ExperimentMembership.Effective(record,
            [new("baseline-event", "pair", "baseline", baseline, "sha", "matched briefs", At),
             new("comparison-event", "pair", "comparison", comparison, "sha", "matched briefs", At)]);

    private static ExperimentReadingResult Evaluate(ExperimentRecord record, IReadOnlyCollection<Goal> goals) =>
        ExperimentReading.Evaluate(record, goals, new Dictionary<string, DateTimeOffset>(), [],
            At.AddDays(2), null, ExperimentHostLedgers.Empty);

    private static IReadOnlyCollection<Goal> Goals(GoalStatus memberStatus) => Hydrate(Snapshots(memberStatus));

    private static GoalSnapshot[] Snapshots(GoalStatus memberStatus) =>
        [Snapshot(BaselineId, memberStatus), Snapshot(ComparisonId, memberStatus),
         .. Enumerable.Range(1, 13).Select(index => Snapshot($"unrelated-{index}",
             (index % 4) switch { 0 => GoalStatus.Completed, 1 => GoalStatus.Failed,
                 2 => GoalStatus.Cancelled, _ => GoalStatus.Superseded }, At.AddHours(index)))];

    private static GoalSnapshot Snapshot(string id, GoalStatus status, DateTimeOffset? dispatch = null) =>
        new(id, id, status,
            [new TaskSnapshot(id + "-task", "implement", AgentRole.Developer,
                status == GoalStatus.Active ? WorkTaskStatus.Pending : WorkTaskStatus.Completed,
                null, null, null, [], null, null,
                DispatchHistory: dispatch is { } at ? [new("worker", "command", "root", at)] : [])], []);

    private static IReadOnlyCollection<Goal> Hydrate(GoalSnapshot[] snapshots) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, [])).Goals;
}
