using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its workspace and all evaluation times are fixed.
public sealed class ExperimentMembershipReadingTests
{
    [Fact]
    public void Effective_RecordedPairExtendsOnlyCohortsAndPreservesStopProgress()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = ExperimentAddPairCommandTests.Spec() with { Baseline = new(ExperimentBaselineKind.GoalCohort,
                BaselineGoalIds: [Id('a')], ComparisonGoalIds: [Id('b')], IncrementalMembers: true) };
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            store.AddAsync(spec).GetAwaiter().GetResult();
            var record = store.ListAllAsync().GetAwaiter().GetResult().Single() with { CreatedAt = ExperimentMemberStoreTests.At };
            ExperimentMemberStoreTests.Add(store, record.Id, Id('c'), Id('d'));
            ExperimentAddPairCommandTests.Seed(workspace, landed: true);
            var goals = ExperimentGoals.Read(workspace.SqliteStatePath);
            var before = Evaluate(record, goals);
            var events = store.ListMemberEventsAsync(record.Id).GetAwaiter().GetResult();
            var effective = ExperimentMembership.Effective(record, events);
            var after = Evaluate(effective, goals);
            Assert.Equal(new[] { Id('a'), Id('c') }, effective.Spec.Baseline.BaselineGoalIds);
            Assert.Equal(new[] { Id('b'), Id('d') }, effective.Spec.Baseline.ComparisonGoalIds);
            Assert.Equal($"baseline={Id('a')},{Id('c')} comparison={Id('b')},{Id('d')}", after.Goals);
            Assert.Equal("cohort goals", after.GoalsLabel);
            Assert.DoesNotContain(Id('e'), after.Goals!);
            Assert.Equal(12, before.StopTarget);
            Assert.Equal(before.StopTarget, after.StopTarget);
            Assert.Equal(before.StopRule, after.StopRule);
            Assert.Equal(6, before.ObservedCount);
            Assert.Equal(before.ObservedCount, after.ObservedCount);
            Assert.Equal(before.StopRuleMet, after.StopRuleMet);
            var baselineTotals = RoundValueReport.Build(goals.Where(goal => goal.Id.Value == Id('a') || goal.Id.Value == Id('c')),
                DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Window;
            var comparisonTotals = RoundValueReport.Build(goals.Where(goal => goal.Id.Value == Id('b') || goal.Id.Value == Id('d')),
                DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Window;
            var metric = after.Metrics.Single(value => value.Metric == "productive-rounds");
            Assert.Equal((double)baselineTotals.Productive, metric.Before);
            Assert.Equal((double)comparisonTotals.Productive, metric.After);
            Assert.Equal(2, baselineTotals.LandedGoals);
            Assert.Equal(2, comparisonTotals.LandedGoals);
            Assert.Equal(new[] { Id('a') }, record.Spec.Baseline.BaselineGoalIds);
        });
    }

    [Fact]
    public void Effective_NoEventsPreservesRecordAndEveryReadingField()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = ExperimentAddPairCommandTests.Spec() with { Baseline = new(ExperimentBaselineKind.GoalCohort,
                BaselineGoalIds: [Id('a')], ComparisonGoalIds: [Id('b')]) };
            var record = new ExperimentRecord("legacy", spec, ExperimentMemberStoreTests.At, ExperimentOutcomeState.Open, null);
            ExperimentAddPairCommandTests.Seed(workspace, landed: true);
            var goals = ExperimentGoals.Read(workspace.SqliteStatePath);
            var effective = ExperimentMembership.Effective(record, []);
            Assert.Same(record, effective);
            var before = Evaluate(record, goals);
            var after = Evaluate(effective, goals);
            Assert.Equal(before.Verdict, after.Verdict);
            Assert.Equal(before.Reason, after.Reason);
            Assert.Equal(before.Goals, after.Goals);
            Assert.Equal(before.GoalsLabel, after.GoalsLabel);
            Assert.Equal(before.ObservedCount, after.ObservedCount);
            Assert.Equal(before.StopTarget, after.StopTarget);
            Assert.Equal(before.StopRule, after.StopRule);
            Assert.Equal(before.StopRuleMet, after.StopRuleMet);
            Assert.Equal(before.GuardrailBreached, after.GuardrailBreached);
            Assert.Equal(before.Metrics, after.Metrics);
        });
    }

    [Fact]
    public void Effective_DeduplicatesIdsWithSpecOrderBeforeEventOrder()
    {
        var spec = ExperimentAddPairCommandTests.Spec() with { Baseline = new(ExperimentBaselineKind.GoalCohort,
            BaselineGoalIds: ["FIRST", "second"], ComparisonGoalIds: ["other"]) };
        var record = new ExperimentRecord("cohort", spec, ExperimentMemberStoreTests.At, ExperimentOutcomeState.Open, null);
        ExperimentMemberEvent Event(string arm, string goal) => new("event", "pair", arm, goal, "sha", "reason", ExperimentMemberStoreTests.At);
        var effective = ExperimentMembership.Effective(record,
            [Event("baseline", "first"), Event("baseline", "third"), Event("baseline", "THIRD"), Event("comparison", "fourth")]);
        Assert.Equal(new[] { "FIRST", "second", "third" }, effective.Spec.Baseline.BaselineGoalIds);
        Assert.Equal(new[] { "other", "fourth" }, effective.Spec.Baseline.ComparisonGoalIds);
    }

    private static string Id(char value) => ExperimentAddPairCommandTests.Id(value);
    private static ExperimentReadingResult Evaluate(ExperimentRecord record, IReadOnlyCollection<Goal> goals) =>
        ExperimentReading.Evaluate(record, goals, new Dictionary<string, DateTimeOffset>(), [],
            ExperimentMemberStoreTests.At.AddDays(2), null, ExperimentHostLedgers.Empty);
}
