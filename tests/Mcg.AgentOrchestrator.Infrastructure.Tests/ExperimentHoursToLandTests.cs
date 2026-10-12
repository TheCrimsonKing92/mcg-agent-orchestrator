using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: fixed timestamps and in-memory snapshots; no shared state or I/O.
public sealed class ExperimentHoursToLandTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cohort_FourLandedGoals_KeepsFasterMeanAndPreservesPooledRounds()
    {
        var goals = Goals();
        Assert.Contains("hours-to-land", ExperimentMetrics.Menu);
        Assert.All(goals, goal =>
        {
            Assert.Equal(Created, Assert.Single(goal.Timeline,
                entry => entry.Kind == ProgressKind.GoalCreated).OccurredAt);
            Assert.DoesNotContain(goal.Timeline, entry => entry.Kind == ProgressKind.TaskDelegated);
        });

        var reading = Evaluate(Spec(), goals, Landings());
        var hours = Metric(reading, "hours-to-land");
        Assert.Equal(3.0, hours.Before);
        Assert.Equal(2.0, hours.After);
        var change = (hours.After!.Value - hours.Before!.Value) / hours.Before.Value * 100;
        Assert.True(change <= -30);
        Assert.False(change > 0);
        Assert.Equal(-100.0 / 3, change, 10);
        Assert.Equal("keep", reading.Verdict);
        Assert.False(reading.GuardrailBreached);

        var before = Totals(goals.Where(goal => goal.Id.Value == Id('a') || goal.Id.Value == Id('b')));
        var after = Totals(goals.Where(goal => goal.Id.Value == Id('c') || goal.Id.Value == Id('d')));
        Assert.Equal(2, before.LandedGoals);
        Assert.Equal(2, after.LandedGoals);
        Assert.NotNull(before.RoundsPerLanding);
        Assert.NotNull(after.RoundsPerLanding);
        var rounds = Metric(reading, "rounds-per-landing");
        Assert.Equal(before.RoundsPerLanding, rounds.Before);
        Assert.Equal(after.RoundsPerLanding, rounds.After);
    }

    [Fact]
    public void Twin_SingleGoalPerSide_EqualsEachGoalsOwnHours()
    {
        var goals = Goals();
        var landings = Landings();
        var spec = Spec() with
        {
            Baseline = new(ExperimentBaselineKind.TwinGoal,
                TwinGoalId: Id('a'), ComparisonGoalId: Id('c'))
        };
        var reading = Evaluate(spec, goals, landings);
        var hours = Metric(reading, "hours-to-land");
        Assert.Equal(2.0, hours.Before);
        Assert.Equal(1.0, hours.After);
        Assert.Equal(OwnHours('a'), hours.Before);
        Assert.Equal(OwnHours('c'), hours.After);
        Assert.Equal("keep", reading.Verdict);

        double OwnHours(char id) => GoalTimingReport.Build(goals.Single(goal => goal.Id.Value == Id(id)),
            new GoalTimingReportContext(LandedAt: landings[Id(id)], LandingSource: "experiment-landing-event"))
            .TotalDuration.TotalHours;
    }

    [Fact]
    public void Cohort_AliasesOfOneGoal_CountsThatGoalOnceInMean()
    {
        var spec = Spec() with
        {
            Baseline = Spec().Baseline with { BaselineGoalIds = [Id('a'), "aaaa", Id('b')] }
        };
        var reading = Evaluate(spec, Goals(), Landings());
        Assert.Equal(3.0, Metric(reading, "hours-to-land").Before);
        Assert.Equal("keep", reading.Verdict);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cohort_UnknownMember_NullsOnlyItsSideAndNamesMissingGoal(bool baseline)
    {
        var spec = Spec();
        spec = spec with
        {
            Baseline = baseline ? spec.Baseline with { BaselineGoalIds = [Id('a'), "unknown"] } :
                spec.Baseline with { ComparisonGoalIds = [Id('c'), "unknown"] }
        };
        var reading = Evaluate(spec, Goals(), Landings());
        var hours = Metric(reading, "hours-to-land");
        Assert.Null(baseline ? hours.Before : hours.After);
        Assert.Equal(baseline ? 2.0 : 3.0, baseline ? hours.After : hours.Before);
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.Contains($"{(baseline ? "baseline" : "comparison")} goal 'unknown' not found", reading.Reason);
        Assert.DoesNotContain("has no landing time", reading.Reason);
    }

    [Fact]
    public void Cohort_SharedMember_NullsComparisonAndNamesSharedGoal()
    {
        var spec = Spec() with
        {
            Baseline = Spec().Baseline with { ComparisonGoalIds = [Id('a'), Id('c')] }
        };
        var reading = Evaluate(spec, Goals(), Landings());
        var hours = Metric(reading, "hours-to-land");
        Assert.Equal(3.0, hours.Before);
        Assert.Null(hours.After);
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.Contains($"baseline and comparison cohorts share goal '{Id('a')}'", reading.Reason);
        Assert.DoesNotContain("has no landing time", reading.Reason);
    }

    [Theory]
    [InlineData('a')]
    [InlineData('b')]
    [InlineData('c')]
    [InlineData('d')]
    public void Cohort_MissingLanding_MakesKeepInconclusiveWithoutChangingRounds(char missing)
    {
        var goals = Goals();
        var landings = Landings();
        var complete = Evaluate(Spec(), goals, landings);
        Assert.Equal("keep", complete.Verdict);
        Assert.True(landings.Remove(Id(missing)));

        var reading = Evaluate(Spec(), goals, landings);
        var hours = Metric(reading, "hours-to-land");
        var baseline = missing is 'a' or 'b';
        Assert.Null(baseline ? hours.Before : hours.After);
        Assert.Equal(baseline ? 2.0 : 3.0, baseline ? hours.After : hours.Before);
        var rounds = Metric(reading, "rounds-per-landing");
        Assert.NotNull(rounds.Before);
        Assert.NotNull(rounds.After);
        Assert.Equal(Metric(complete, "rounds-per-landing"), rounds);
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.Contains($"{(baseline ? "baseline" : "comparison")} goal '{Id(missing)}' has no landing time", reading.Reason);
    }

    [Theory]
    [InlineData('a')]
    [InlineData('c')]
    public void Twin_MissingLanding_PreservesRoundsAndOppositeTime(char missing)
    {
        var spec = Spec() with
        {
            Baseline = new(ExperimentBaselineKind.TwinGoal,
                TwinGoalId: Id('a'), ComparisonGoalId: Id('c'))
        };
        var goals = Goals();
        var landings = Landings();
        var complete = Evaluate(spec, goals, landings);
        Assert.True(landings.Remove(Id(missing)));
        var reading = Evaluate(spec, goals, landings);
        var hours = Metric(reading, "hours-to-land");
        Assert.Null(missing == 'a' ? hours.Before : hours.After);
        Assert.Equal(missing == 'a' ? 1.0 : 2.0, missing == 'a' ? hours.After : hours.Before);
        Assert.Equal(Metric(complete, "rounds-per-landing"), Metric(reading, "rounds-per-landing"));
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.Contains("has no landing time", reading.Reason);
    }

    [Theory]
    [InlineData('a')]
    [InlineData('c')]
    public void Cohort_UnlandedMember_PreservesExistingDiagnostic(char unlanded)
    {
        var reading = Evaluate(Spec(), Goals(unlanded), Landings());
        var hours = Metric(reading, "hours-to-land");
        Assert.Null(unlanded == 'a' ? hours.Before : hours.After);
        Assert.Equal(unlanded == 'a' ? 2.0 : 3.0, unlanded == 'a' ? hours.After : hours.Before);
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.Contains($"goal '{Id(unlanded)}' has not landed", reading.Reason);
        Assert.DoesNotContain("has no landing time", reading.Reason);
    }

    [Fact]
    public void Window_TimeRuleWithLandedRoundTotals_IsUnavailableWithoutThrowing()
    {
        var goals = Goals();
        var until = Created.AddMinutes(30);
        Assert.Equal(2, RoundValueReport.Build(goals, Created, until).Window.LandedGoals);
        Assert.Equal(2, RoundValueReport.Build(goals, until, Created.AddDays(1)).Window.LandedGoals);
        var spec = Spec() with { Baseline = new(ExperimentBaselineKind.BeforeAfterWindow, Created, until) };

        var reading = Evaluate(spec, goals, Landings());
        var hours = Metric(reading, "hours-to-land");
        Assert.Null(hours.Before);
        Assert.Null(hours.After);
        Assert.NotNull(Metric(reading, "rounds-per-landing").Before);
        Assert.NotNull(Metric(reading, "rounds-per-landing").After);
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.Contains("hours-to-land", reading.Reason);
    }

    [Fact]
    public void AlternatingGates_TimeMetric_PreservesExistingRefusalWithoutThrowing()
    {
        var spec = Spec() with
        {
            Baseline = new(ExperimentBaselineKind.AlternatingGates, Created, Created.AddMinutes(30)),
            Metrics = ["hours-to-land"],
            Guardrail = new("gate-seconds", new("gate-seconds", ">", 1000))
        };
        var reading = Evaluate(spec, Goals(), Landings());
        Assert.Equal("unavailable", reading.Verdict);
        Assert.Equal("baseline kind alternating-gates not computable in this slice", reading.Reason);
        Assert.Empty(reading.Metrics);
    }

    [Fact]
    public void Mean_EmptySide_ReturnsNullWithoutDiagnostics()
    {
        var diagnostics = new List<string>();
        Assert.Null(ExperimentHoursToLand.Mean([], Landings(), diagnostics, "baseline"));
        Assert.Empty(diagnostics);
    }

    private static string Id(char prefix) => new(prefix, 32);

    private static IReadOnlyCollection<Goal> Goals(char? unlanded = null)
    {
        GoalSnapshot Snapshot(char prefix, int rounds, int dispatchMinute)
        {
            var id = Id(prefix);
            var tasks = Enumerable.Range(0, rounds).Select(index =>
                new TaskSnapshot($"{id}-{index}", "implementation", AgentRole.Developer, WorkTaskStatus.Completed,
                    null, null, null, [], null, null, DispatchHistory:
                    [new TaskDispatchSnapshot("worker", "command", "root",
                        Created.AddMinutes(dispatchMinute).AddSeconds(index))])).ToArray();
            var events = new[] { new ProgressEventSnapshot(id, null, ProgressKind.GoalCreated, "created", Created) }
                .Concat(tasks.Select((task, index) => new ProgressEventSnapshot(id, task.Id,
                    ProgressKind.TaskCompleted, "done", Created.AddMinutes(dispatchMinute + 1).AddSeconds(index))))
                .ToArray();
            return new(id, id, prefix == unlanded ? GoalStatus.Active : GoalStatus.Completed, tasks, events);
        }
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [Snapshot('a', 4, 1), Snapshot('b', 2, 1), Snapshot('c', 3, 31), Snapshot('d', 1, 31)], [])).Goals;
    }

    private static Dictionary<string, DateTimeOffset> Landings() => new()
    {
        [Id('a')] = Created.AddHours(2), [Id('b')] = Created.AddHours(4),
        [Id('c')] = Created.AddHours(1), [Id('d')] = Created.AddHours(3)
    };

    private static ExperimentSpec Spec() => new("shorter time to land",
        new(ExperimentInterventionKind.BriefOrPromptChange, "brief process trial"),
        new(ExperimentBaselineKind.GoalCohort,
            BaselineGoalIds: [Id('a'), Id('b')], ComparisonGoalIds: [Id('c'), Id('d')]),
        ["hours-to-land", "rounds-per-landing"],
        new("productive-rounds", new("productive-rounds", ">", 1000)),
        new(4, ExperimentStopUnit.Goals),
        new([new("hours-to-land", "<=", -30)], [new("hours-to-land", ">", 0)]));

    private static ExperimentReadingResult Evaluate(ExperimentSpec spec, IReadOnlyCollection<Goal> goals,
        IReadOnlyDictionary<string, DateTimeOffset> landings) => ExperimentReading.Evaluate(
        new ExperimentRecord("time-trial", spec, Created, ExperimentOutcomeState.Open, null),
        goals, landings, [], Created.AddDays(1), null, ExperimentHostLedgers.Empty);

    private static ExperimentMetricReading Metric(ExperimentReadingResult reading, string name) =>
        Assert.Single(reading.Metrics, metric => metric.Metric == name);

    private static RoundValueTotals Totals(IEnumerable<Goal> goals) =>
        RoundValueReport.Build(goals, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Window;
}
