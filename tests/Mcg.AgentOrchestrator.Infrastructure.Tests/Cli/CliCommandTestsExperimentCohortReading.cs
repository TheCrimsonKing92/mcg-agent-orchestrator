using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its database and uses the existing AsyncLocal console capture.
public sealed class CliCommandTestsExperimentCohortReading
{
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-10-04T00:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("baseline", "null")]
    [InlineData("baseline", "empty")]
    [InlineData("baseline", "blank")]
    [InlineData("baseline", "null-entry")]
    [InlineData("baseline", "repeat")]
    [InlineData("comparison", "null")]
    [InlineData("comparison", "empty")]
    [InlineData("comparison", "blank")]
    [InlineData("comparison", "null-entry")]
    [InlineData("comparison", "repeat")]
    [InlineData("comparison", "shared")]
    public void Add_CohortRejectsNamedFieldWithoutPersisting(string side, string fault)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = Spec();
            IReadOnlyList<string>? ids = fault switch
            {
                "null" => null, "empty" => [], "blank" => ["cccc", " "],
                "null-entry" => ["cccc", null!], "repeat" => ["cccc", " CCCC "],
                "shared" => [" AAAA "], _ => throw new ArgumentException(fault)
            };
            var invalid = spec with
            {
                Baseline = side == "baseline" ? spec.Baseline with { BaselineGoalIds = ids } :
                    spec.Baseline with { ComparisonGoalIds = ids }
            };
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var error = Assert.Throws<ArgumentException>(() => CliCommandTestsExperiments.Add(workspace, invalid));
            Assert.Contains(side == "baseline" ? "baseline.baselineGoalIds" : "baseline.comparisonGoalIds", error.Message);
            Assert.Equal(0, store.CountAsync().GetAwaiter().GetResult());
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            Assert.Equal(spec.Baseline.BaselineGoalIds, record.Spec.Baseline.BaselineGoalIds);
            Assert.Equal(spec.Baseline.ComparisonGoalIds, record.Spec.Baseline.ComparisonGoalIds);
            var output = Show(workspace, record.Id);
            Assert.Contains("baseline kind: goal-cohort", output);
            Assert.Contains("baseline cohort goals: aaaa, bbbb", output);
            Assert.Contains("comparison cohort goals: cccc, dddd", output);
        });
    }

    [Fact]
    public void Add_OneMemberPerSideIsValidWithoutResolvingGoals()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = Spec() with { Baseline = new(ExperimentBaselineKind.GoalCohort,
                BaselineGoalIds: ["unknown-before"], ComparisonGoalIds: ["unknown-after"]) };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            Assert.Equal(new[] { "unknown-before" }, record.Spec.Baseline.BaselineGoalIds);
            Assert.Equal(new[] { "unknown-after" }, record.Spec.Baseline.ComparisonGoalIds);
        });
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.TwinGoal)]
    [InlineData(ExperimentBaselineKind.BeforeAfterWindow)]
    public void Show_LegacyBaselineJsonWithoutCohortKeysLoadsUnchanged(ExperimentBaselineKind kind)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Seed(workspace);
            var spec = CliCommandTestsExperiments.Spec();
            if (kind == ExperimentBaselineKind.TwinGoal)
                spec = spec with { Baseline = new(kind, TwinGoalId: "aaaa", ComparisonGoalId: "cccc") };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var expected = Show(workspace, record.Id);
            var baseline = JsonSerializer.SerializeToNode(record.Spec.Baseline, ExperimentStore.JsonOptions)!.AsObject();
            Assert.True(baseline.Remove("baselineGoalIds"));
            Assert.True(baseline.Remove("comparisonGoalIds"));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = workspace.ExperimentStorePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE experiments SET baseline_json = $baseline WHERE id = $id";
                command.Parameters.AddWithValue("$baseline", baseline.ToJsonString());
                command.Parameters.AddWithValue("$id", record.Id);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var loaded = new ExperimentStore(workspace.ExperimentStorePath).ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            Assert.Null(loaded.Spec.Baseline.BaselineGoalIds);
            Assert.Null(loaded.Spec.Baseline.ComparisonGoalIds);
            Assert.Equal(spec.Baseline, loaded.Spec.Baseline);
            Assert.Equal(expected, Show(workspace, record.Id));
        });
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("revert")]
    [InlineData("breach")]
    public void Show_CohortPoolsTotalsAndSharesVerdictRules(string scenario)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var baselineGoals = kernel.Goals.Where(g => g.Id.Value[0] is 'a' or 'b').ToArray();
            var comparisonGoals = kernel.Goals.Where(g => g.Id.Value[0] is 'c' or 'd').ToArray();
            var before = Totals(baselineGoals);
            var after = Totals(comparisonGoals);
            var perGoalProductive = comparisonGoals.Select(g => Totals([g]).Productive).ToArray();
            Assert.Equal(2, perGoalProductive.Distinct().Count());
            Assert.NotEqual(perGoalProductive.Average(), (double)after.Productive);
            Assert.Equal((double)before.Rounds / baselineGoals.Length, before.RoundsPerLanding);
            Assert.Equal((double)after.Rounds / comparisonGoals.Length, after.RoundsPerLanding);
            var change = (after.RoundsPerLanding!.Value - before.RoundsPerLanding!.Value) / before.RoundsPerLanding.Value * 100;
            var guardrailChange = (double)(after.Productive - before.Productive) / before.Productive * 100;
            var threshold = change + (scenario == "revert" ? -1 : 1);
            var spec = Spec() with
            {
                Baseline = new(ExperimentBaselineKind.GoalCohort, BaselineGoalIds: [" AAAA ", "bbbb"],
                    ComparisonGoalIds: comparisonGoals.Select(g => g.Id.Value).ToArray()),
                Metrics = ["rounds-per-landing", "expected-overhead-rounds", "wasted-rounds"],
                DecisionRule = new([new("rounds-per-landing", "<=", threshold)], [new("rounds-per-landing", ">", threshold)]),
                Guardrail = new("productive-rounds", new("productive-rounds", ">", guardrailChange + (scenario == "breach" ? -1 : 1)))
            };
            var output = Show(workspace, CliCommandTestsExperiments.Add(workspace, spec).Id);
            Assert.Contains($"cohort goals: baseline={string.Join(",", baselineGoals.Select(g => g.Id.Value))} comparison={string.Join(",", comparisonGoals.Select(g => g.Id.Value))}", output);
            Assert.DoesNotContain("twin goals:", output);
            AssertMetric(output, "rounds per landing", before.RoundsPerLanding, after.RoundsPerLanding);
            AssertMetric(output, "productive rounds", before.Productive, after.Productive);
            AssertMetric(output, "expected overhead rounds", before.ExpectedOverhead, after.ExpectedOverhead);
            AssertMetric(output, "wasted rounds", before.Wasted, after.Wasted);
            Assert.Contains($"reading: {(scenario == "breach" ? "inconclusive" : scenario)} (pre-registered rules evaluated", output);
            Assert.Equal(scenario == "breach", output.Contains("guardrail breached: productive-rounds", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Show_CohortLandingsPerHourIsUnavailable(bool requiredByRule)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Seed(workspace);
            var spec = Spec() with { Metrics = ["rounds-per-landing", "landings-per-hour"] };
            if (requiredByRule) spec = spec with { DecisionRule = new(
                [new("landings-per-hour", "<=", 0)], [new("landings-per-hour", ">", 0)]) };
            var output = Show(workspace, CliCommandTestsExperiments.Add(workspace, spec).Id);
            AssertMetric(output, "landings per hour", null, null);
            Assert.Contains(requiredByRule ? "reading: inconclusive" : "reading: keep", output);
        });
    }

    [Fact]
    public void Evaluate_CohortPrefixAndFullIdOfOneGoalDoNotDoubleCount()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var goal = kernel.Goals.Single(g => g.Id.Value[0] == 'c');
            var spec = Spec() with { Baseline = Spec().Baseline with { ComparisonGoalIds = ["cccc", goal.Id.Value] } };
            var result = Evaluate(new("cohort", spec, AsOf, ExperimentOutcomeState.Open, null), kernel.Goals);
            var totals = Totals([goal]);
            Assert.Equal(totals.RoundsPerLanding, result.Metrics.Single(m => m.Metric == "rounds-per-landing").After);
            Assert.Equal((double)totals.Productive, result.Metrics.Single(m => m.Metric == "productive-rounds").After);
            Assert.Equal($"baseline={new string('a', 32)},{new string('b', 32)} comparison={goal.Id.Value}", result.Goals);
            Assert.Equal("keep", result.Verdict);
        });
    }

    [Theory]
    [InlineData("baseline", "unknown")]
    [InlineData("baseline", "pending")]
    [InlineData("baseline", "ambiguous")]
    [InlineData("baseline", "no-rounds")]
    [InlineData("comparison", "unknown")]
    [InlineData("comparison", "pending")]
    [InlineData("comparison", "ambiguous")]
    [InlineData("comparison", "no-rounds")]
    public void Show_CohortWithOneBadMemberIsInconclusive(string side, string fault)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var goals = kernel.Goals.ToArray();
            var member = fault switch { "unknown" => "ffff", "pending" => "eeee", "no-rounds" => "9999", _ => "cccc" };
            var diagnostic = fault switch { "unknown" => "not found", "pending" => "has not landed",
                "no-rounds" => "has no landed round totals", _ => "is ambiguous" };
            if (fault == "ambiguous")
                goals = goals.Append(new Goal(new GoalId(new string('c', 32) + "-other"), "shared prefix",
                    [new TaskSpec(new TaskId("other-task"), "pending", AgentRole.Developer)])).ToArray();
            // Full ids keep the control valid even in the ambiguous-prefix case.
            var spec = Spec() with { Baseline = new(ExperimentBaselineKind.GoalCohort,
                BaselineGoalIds: [new string('a', 32), new string('b', 32)],
                ComparisonGoalIds: [new string('c', 32), new string('d', 32)]) };
            var record = new ExperimentRecord("cohort", spec, AsOf, ExperimentOutcomeState.Open, null);
            Assert.Equal("keep", Evaluate(record, goals).Verdict);
            var invalid = spec.Baseline with
            {
                BaselineGoalIds = side == "baseline" ? spec.Baseline.BaselineGoalIds!.Append(member).ToArray() : spec.Baseline.BaselineGoalIds,
                ComparisonGoalIds = side == "comparison" ? spec.Baseline.ComparisonGoalIds!.Append(member).ToArray() : spec.Baseline.ComparisonGoalIds
            };
            var result = Evaluate(record with { Spec = spec with { Baseline = invalid } }, goals);
            Assert.Equal("inconclusive", result.Verdict);
            Assert.Contains($"{side} goal '", result.Reason);
            Assert.Contains(member, result.Reason);
            Assert.Contains(diagnostic, result.Reason);
            Assert.All(result.Metrics, metric => Assert.Null(side == "baseline" ? metric.Before : metric.After));
            Assert.All(result.Metrics, metric => Assert.NotNull(side == "baseline" ? metric.After : metric.Before));
        });
    }

    [Fact]
    public void Evaluate_CrossSideAliasesCannotProduceAComparison()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var spec = Spec() with { Baseline = Spec().Baseline with { ComparisonGoalIds = [new string('a', 32), "cccc"] } };
            var result = Evaluate(new("cohort", spec, AsOf, ExperimentOutcomeState.Open, null), kernel.Goals);
            Assert.Equal("inconclusive", result.Verdict);
            Assert.Contains($"baseline and comparison cohorts share goal '{new string('a', 32)}'", result.Reason);
            Assert.All(result.Metrics, metric => Assert.Null(metric.After));
        });
    }

    [Fact]
    public void Runbook_DescribesGoalCohortBaseline()
    {
        var runbook = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operator-runbook.md"));
        Assert.Contains("`goal-cohort` (with `baselineGoalIds` and `comparisonGoalIds`)", runbook);
        Assert.Contains("A goal-cohort reading pools each side's round totals: rounds per landing is the side's total rounds divided by its total landed goals", runbook);
        Assert.Contains("Any unknown, ambiguous, unlanded or totals-less member makes that side unavailable and the reading inconclusive", runbook);
    }

    private static ExperimentSpec Spec() => CliCommandTestsExperiments.Spec() with
    {
        Baseline = new(ExperimentBaselineKind.GoalCohort, BaselineGoalIds: ["aaaa", "bbbb"], ComparisonGoalIds: ["cccc", "dddd"]),
        Metrics = ["rounds-per-landing"],
        DecisionRule = new([new("rounds-per-landing", "<=", 0)], [new("rounds-per-landing", ">", 0)]),
        Guardrail = new("productive-rounds", new("productive-rounds", ">", 1000))
    };

    private static AgentOrchestratorKernel Seed(OrchestratorWorkspace workspace)
    {
        GoalSnapshot Goal(char prefix, GoalStatus status, params AgentRole[] roles)
        {
            var id = new string(prefix, 32);
            var at = AsOf.AddDays(-3);
            var tasks = roles.Select((role, i) => new TaskSnapshot($"{id}-{i}", role.ToString(), role, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [new TaskDispatchSnapshot("worker", "command", "root", at.AddMinutes(i))])).ToArray();
            if (tasks.Length == 0)
                tasks = [new TaskSnapshot($"{id}-0", "no dispatch history", AgentRole.Developer, WorkTaskStatus.Completed,
                    null, null, null, [], null, null, DispatchHistory: [])];
            var events = tasks.Select((task, i) => new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted,
                "done", at.AddMinutes(i).AddSeconds(30))).ToArray();
            return new(id, id, status, tasks, events);
        }
        var snapshots = new[]
        {
            Goal('a', GoalStatus.Completed, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Goal('b', GoalStatus.Completed, AgentRole.Developer, AgentRole.Tester),
            Goal('c', GoalStatus.Completed, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Goal('d', GoalStatus.Completed, AgentRole.Developer),
            Goal('e', GoalStatus.Active, AgentRole.Developer),
            Goal('9', GoalStatus.Completed)
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, []));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        return kernel;
    }

    private static RoundValueTotals Totals(IEnumerable<Goal> goals) =>
        RoundValueReport.Build(goals, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Window;

    private static ExperimentReadingResult Evaluate(ExperimentRecord record, IReadOnlyCollection<Goal> goals) =>
        ExperimentReading.Evaluate(record, goals, new Dictionary<string, DateTimeOffset>(), [], AsOf, null);

    private static string Show(OrchestratorWorkspace workspace, string id) =>
        CliCommandTestsExperiments.Execute(["experiment-show", id, "--as-of", AsOf.ToString("O")], workspace);

    private static void AssertMetric(string output, string label, double? before, double? after)
    {
        string Format(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "unavailable";
        Assert.Contains($"{label}: baseline={Format(before)} comparison={Format(after)}{Environment.NewLine}", output);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") => VerifiedRepositoryRoot.Find(sourcePath);
}
