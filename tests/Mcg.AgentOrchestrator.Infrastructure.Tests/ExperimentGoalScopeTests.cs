using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExperimentGoalScopeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private const string A = "aaaa1111000000000000000000000000";
    private const string A2 = "aaaa1111111111111111111111111111";
    private const string B = "bbbb2222000000000000000000000000";

    [Fact]
    public void For_IncrementalCohort_IncludesAllTrimmedPrefixMatchesOnly()
    {
        var scope = ExperimentGoalScope.For([Record(new(ExperimentBaselineKind.GoalCohort,
            BaselineGoalIds: ["  AAAA1111 "], ComparisonGoalIds: [""], IncrementalMembers: true))]);

        Assert.True(scope.Includes(A, "not-a-time"));
        Assert.True(scope.Includes(A2, Stamp(At.AddDays(-30))));
        Assert.False(scope.Includes(B, Stamp(At)));
        Assert.Null(scope.ActiveSince);
        Assert.False(scope.IsEmpty);
    }

    [Fact]
    public void For_TwinWithGates_IncludesBothReferencesOnly()
    {
        var scope = ExperimentGoalScope.For([Record(new(ExperimentBaselineKind.TwinGoal,
            TwinGoalId: A, ComparisonGoalId: B), ExperimentStopUnit.Gates)]);

        Assert.True(scope.Includes(A, "not-a-time"));
        Assert.True(scope.Includes(B, "not-a-time"));
        Assert.False(scope.Includes(A2, Stamp(At)));
        Assert.Null(scope.ActiveSince);
    }

    [Theory]
    [InlineData(ExperimentStopUnit.Gates)]
    [InlineData(ExperimentStopUnit.Ticks)]
    public void For_AlternatingGatesWithoutReferences_IsEmpty(ExperimentStopUnit unit)
    {
        var scope = ExperimentGoalScope.For([Record(new(ExperimentBaselineKind.AlternatingGates), unit)]);

        Assert.True(scope.IsEmpty);
        Assert.Null(scope.ActiveSince);
        Assert.False(scope.Includes(A, "not-a-time"));
    }

    [Fact]
    public void For_Window_IncludesMarginAndUnparsableTimes()
    {
        var scope = ExperimentGoalScope.For([Record(new(ExperimentBaselineKind.BeforeAfterWindow,
            Since: At, Until: At.AddDays(1)), ExperimentStopUnit.Gates)]);

        Assert.Equal(At, scope.ActiveSince);
        Assert.True(scope.Includes(A, Stamp(At.AddHours(-23))));
        Assert.True(scope.Includes(A, Stamp(At.AddHours(-24))));
        Assert.True(scope.Includes(B, "not-a-time"));
        Assert.False(scope.Includes(B, Stamp(At.AddHours(-25))));
    }

    [Fact]
    public void For_NonIncrementalGoals_IncludesRecentRowsAndOldMembers()
    {
        var scope = ExperimentGoalScope.For([Record(new(ExperimentBaselineKind.GoalCohort,
            BaselineGoalIds: [A], ComparisonGoalIds: ["  "]))]);

        Assert.Equal(At, scope.ActiveSince);
        Assert.True(scope.Includes(B, Stamp(At.AddHours(-1))));
        Assert.False(scope.Includes(B, Stamp(At.AddDays(-2))));
        Assert.True(scope.Includes(A, Stamp(At.AddDays(-30))));
    }

    [Fact]
    public void For_MultipleTimeScopes_UsesEarliestSinceOrComparisonStart()
    {
        var scope = ExperimentGoalScope.For([
            Record(new(ExperimentBaselineKind.BeforeAfterWindow, Since: At.AddDays(2), Until: At)),
            Record(new(ExperimentBaselineKind.GoalCohort, Since: At.AddDays(-2), Until: At.AddDays(1))) ]);

        Assert.Equal(At.AddDays(-2), scope.ActiveSince);
        Assert.True(scope.Includes(B, Stamp(At.AddDays(-3))));
        Assert.False(scope.Includes(B, Stamp(At.AddDays(-3).AddTicks(-1))));
        var comparisonBound = ExperimentGoalScope.For([
            Record(new(ExperimentBaselineKind.BeforeAfterWindow, Since: At.AddDays(2), Until: At))]);
        Assert.Equal(At, comparisonBound.ActiveSince);
    }

    [Fact]
    public void For_NoRecords_IsEmptyWhileAllIncludesEveryRow()
    {
        Assert.True(ExperimentGoalScope.For([]).IsEmpty);
        Assert.False(ExperimentGoalScope.For([]).Includes(A, "not-a-time"));
        Assert.True(ExperimentGoalScope.Empty.IsEmpty);
        Assert.False(ExperimentGoalScope.Empty.Includes(B, Stamp(At)));
        Assert.False(ExperimentGoalScope.All.IsEmpty);
        Assert.True(ExperimentGoalScope.All.Includes(A, "not-a-time"));
        Assert.True(ExperimentGoalScope.All.Includes(B, Stamp(At.AddYears(-10))));
    }

    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static ExperimentRecord Record(ExperimentBaseline baseline,
        ExperimentStopUnit unit = ExperimentStopUnit.Goals) =>
        new(new string('e', 32), new("Scope", new(ExperimentInterventionKind.BriefOrPromptChange, "Scope"),
            baseline, ["rounds-per-landing"], new("productive-rounds", new("productive-rounds", ">", 1000)),
            new(12, unit), new([], [])), At, ExperimentOutcomeState.Open, null);
}
