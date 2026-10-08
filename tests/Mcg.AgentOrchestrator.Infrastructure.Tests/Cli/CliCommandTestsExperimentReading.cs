using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: explicit windows, local databases and AsyncLocal console capture.
public sealed class CliCommandTestsExperimentReading
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Until = Since.AddDays(1);
    private static readonly DateTimeOffset AsOf = Since.AddDays(2);

    [Theory]
    [InlineData("keep")]
    [InlineData("breach")]
    [InlineData("missing")]
    [InlineData("unsupported")]
    public void Show_ReadingBlockIsUnchanged(string scenario)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var spec = CliCommandTestsExperiments.Spec();
            if (scenario == "breach") spec = spec with { Guardrail = new("productive-rounds", new("productive-rounds", ">", 20)) };
            if (scenario == "unsupported") spec = spec with
            {
                Baseline = new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "twin"), StopRule = new(4, ExperimentStopUnit.Ticks)
            };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var output = Show(workspace, record.Id, scenario == "missing" ? Until : AsOf);
            string Format(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "unavailable";
            var baseline = RoundValueReport.Build(kernel.Goals, Since, Until).Window;
            var comparison = RoundValueReport.Build(kernel.Goals, Until, AsOf).Window;
            var lines = new List<string>();
            if (scenario == "unsupported")
            {
                lines.Add("stop rule: unavailable of 4 ticks (progress unavailable)");
                lines.Add("reading: unavailable (baseline kind twin-goal not computable in this slice)");
            }
            else
            {
                var missing = scenario == "missing";
                lines.Add(missing ? "stop rule: 0 of 2 goals (not met)" : "stop rule: 2 of 2 goals (met)");
                lines.Add($"rounds per landing: baseline={Format(baseline.RoundsPerLanding)} comparison={Format(missing ? null : comparison.RoundsPerLanding)}");
                lines.Add($"landings per hour: baseline={Format(1.0 / 24)} comparison={Format(missing ? null : 2.0 / 24)}");
                lines.Add($"productive rounds: baseline={Format(baseline.Productive)} comparison={Format(missing ? null : comparison.Productive)}");
                lines.Add(scenario switch
                {
                    "missing" => "reading: inconclusive (unavailable comparison or zero baseline: rounds-per-landing, productive-rounds)",
                    "breach" => $"reading: inconclusive (pre-registered rules evaluated; guardrail breached: productive-rounds baseline={Format(baseline.Productive)} comparison={Format(comparison.Productive)})",
                    _ => "reading: keep (pre-registered rules evaluated)"
                });
            }
            lines.Add("outcome: open");
            lines.Add("overlaps: none");
            var expected = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            var start = output.IndexOf("stop rule: ", StringComparison.Ordinal);
            Assert.True(start >= 0, output);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(output[start..]));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_MissingDecisionMetricStillReportsGuardrailBreach(bool missingKeep)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var spec = CliCommandTestsExperiments.Spec() with
            {
                DecisionRule = new([new(missingKeep ? "landings-per-hour" : "rounds-per-landing", "<", 0)],
                    [new(missingKeep ? "rounds-per-landing" : "landings-per-hour", ">", 0)]),
                Guardrail = new("productive-rounds", new("productive-rounds", ">", 20))
            };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            ExperimentReadingResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() => result = ExperimentReading.Evaluate(record, kernel.Goals,
                new Dictionary<string, DateTimeOffset>(), [], AsOf));
            Assert.Empty(output);
            Assert.NotNull(result);
            Assert.True(result.GuardrailBreached);
            Assert.Equal("inconclusive", result.Verdict);
            Assert.Equal("unavailable comparison or zero baseline: landings-per-hour", result.Reason);
        });
    }

    [Theory]
    [InlineData(-10, 10, 0, "keep")]
    [InlineData(-80, -60, 0, "revert")]
    [InlineData(-80, 10, 0, "inconclusive")]
    [InlineData(-10, 10, 20, "inconclusive")]
    [InlineData(-10, -60, 0, "inconclusive")]
    [InlineData(-80, -60, 20, "revert")]
    public void Show_ComputesReportMetricsAndEvaluatesRulesWithGuardrail(double keepThreshold, double revertThreshold,
        double guardrailThreshold, string verdict)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var spec = CliCommandTestsExperiments.Spec() with
            {
                Metrics = ["rounds-per-landing", "landings-per-hour", "wasted-rounds"],
                DecisionRule = new([new("rounds-per-landing", "<=", keepThreshold)], [new("rounds-per-landing", ">=", revertThreshold)]),
                Guardrail = new("productive-rounds", new("productive-rounds", ">", guardrailThreshold == 0 ? 1000 : guardrailThreshold))
            };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var output = Show(workspace, record.Id);
            var report = RoundValueReport.Build(kernel.Goals, Until, AsOf, Since, Until);
            var baseline = RoundValueReport.Build(kernel.Goals, Since, Until).Window;
            AssertMetric(output, "rounds per landing", report.Baseline!.BaselineRoundsPerLanding, report.Baseline.CurrentRoundsPerLanding);
            AssertMetric(output, "productive rounds", baseline.Productive, report.Window.Productive);
            AssertMetric(output, "wasted rounds", baseline.Wasted, report.Window.Wasted);
            AssertMetric(output, "landings per hour", 1.0 / (Until - Since).TotalHours, 2.0 / (AsOf - Until).TotalHours);
            Assert.Contains($"reading: {verdict} (", output);
            Assert.Contains("stop rule: 2 of 2 goals (met)", output);
            if (guardrailThreshold > 0)
            {
                Assert.Contains("guardrail breached: productive-rounds", output);
                Assert.Contains($"baseline={baseline.Productive} comparison={report.Window.Productive}", output);
            }
            // A second record exposes the remaining round class, using the same authoritative report.
            var classes = spec with
            {
                Metrics = ["productive-rounds", "expected-overhead-rounds", "wasted-rounds"],
                Guardrail = new("landings-per-hour", new("landings-per-hour", "<", -50)),
                DecisionRule = new([new("productive-rounds", ">", 0)], [new("productive-rounds", "<", 0)])
            };
            var classOutput = Show(workspace, CliCommandTestsExperiments.Add(workspace, classes).Id);
            AssertMetric(classOutput, "expected overhead rounds", baseline.ExpectedOverhead, report.Window.ExpectedOverhead);
            AssertMetric(classOutput, "productive rounds", baseline.Productive, report.Window.Productive);
            AssertMetric(classOutput, "wasted rounds", baseline.Wasted, report.Window.Wasted);
        });
    }

    [Fact]
    public void Show_RequiresAllConditionsAndKeepsMissingDataInconclusive()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Seed(workspace);
            var spec = CliCommandTestsExperiments.Spec() with
            {
                DecisionRule = new([new("rounds-per-landing", "<", 0), new("landings-per-hour", ">", 200)],
                    [new("rounds-per-landing", ">", 0)])
            };
            Assert.Contains("reading: inconclusive", Show(workspace, CliCommandTestsExperiments.Add(workspace, spec).Id));
            var normal = CliCommandTestsExperiments.Add(workspace, CliCommandTestsExperiments.Spec());
            var emptyComparison = Show(workspace, normal.Id, Until.AddMinutes(1));
            Assert.Contains("rounds per landing: baseline=6 comparison=unavailable", emptyComparison);
            Assert.Contains("landings per hour: baseline=", emptyComparison);
            Assert.Contains("comparison=unavailable", emptyComparison);
            Assert.Contains("reading: inconclusive (unavailable comparison or zero baseline:", emptyComparison);
            var zeroDuration = Show(workspace, normal.Id, Until);
            Assert.Contains("comparison=unavailable", zeroDuration);
            Assert.Contains("reading: inconclusive", zeroDuration);
            // A computed zero is not a viable percentage-change denominator.
            var zeroBaseline = spec with
            {
                Metrics = ["wasted-rounds"], Baseline = new(ExperimentBaselineKind.BeforeAfterWindow, Until, AsOf),
                DecisionRule = new([new("wasted-rounds", "<=", 0)], [new("wasted-rounds", ">", 0)])
            };
            var zeroOutput = Show(workspace, CliCommandTestsExperiments.Add(workspace, zeroBaseline).Id, AsOf.AddDays(1));
            Assert.Contains("wasted rounds: baseline=0 comparison=unavailable", zeroOutput);
            Assert.Contains("reading: inconclusive", zeroOutput);
        });
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.TwinGoal, ExperimentStopUnit.Ticks)]
    [InlineData(ExperimentBaselineKind.AlternatingGates, ExperimentStopUnit.Gates)]
    public void Show_UnsupportedBaselinesAndCountersAreExplicitlyUnavailable(ExperimentBaselineKind kind, ExperimentStopUnit unit)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = CliCommandTestsExperiments.Spec() with
            {
                Baseline = new(kind, Since, Until, "twin-goal-reference"), StopRule = new(4, unit)
            };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var output = Show(workspace, record.Id);
            var kindText = JsonNamingPolicy.KebabCaseLower.ConvertName(kind.ToString());
            var unitText = JsonNamingPolicy.KebabCaseLower.ConvertName(unit.ToString());
            Assert.Contains($"reading: unavailable (baseline kind {kindText} not computable in this slice)", output);
            Assert.Contains($"stop rule: unavailable of 4 {unitText} (progress unavailable)", output);
            Assert.Contains("outcome: open", output);
        });
    }

    internal static AgentOrchestratorKernel Seed(OrchestratorWorkspace workspace)
    {
        GoalSnapshot Goal(string id, DateTimeOffset at, GoalStatus status, params AgentRole[] roles)
        {
            var tasks = roles.Select((role, i) => new TaskSnapshot($"{id}-{i}", role.ToString(), role, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [new TaskDispatchSnapshot("worker", "command", "root", at.AddMinutes(i))])).ToArray();
            var events = tasks.Select((task, i) => new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted,
                "done", at.AddMinutes(i).AddSeconds(30))).ToArray();
            return new(id, id, status, tasks, events);
        }
        var snapshots = new[]
        {
            Goal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Since.AddHours(1), GoalStatus.Completed,
                AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Goal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Since.AddHours(2), GoalStatus.Cancelled, AgentRole.Developer, AgentRole.Tester),
            Goal("cccccccccccccccccccccccccccccccc", Until.AddHours(1), GoalStatus.Completed,
                AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Goal("dddddddddddddddddddddddddddddddd", Until.AddHours(2), GoalStatus.Completed,
                AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Goal("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Until.AddHours(3), GoalStatus.Active, AgentRole.Developer)
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, []));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
        var lines = new List<string> { "malformed", "{}" };
        foreach (var goal in snapshots.Where(g => g.Status == GoalStatus.Completed))
        {
            var at = goal.Id[0] == 'a' ? Since.AddHours(12) : Until.AddHours(12);
            // The earliest receipt is authoritative even if later duplicate receipts cross a window boundary.
            lines.Add(JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = goal.Id, timestamp = at.AddDays(1) }));
            lines.Add(JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = goal.Id, timestamp = at }));
        }
        // A pending goal's event must not be counted as a landing.
        lines.Add(JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = snapshots[4].Id, timestamp = Until.AddHours(12) }));
        File.WriteAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, "seed.jsonl"), lines);
        return kernel;
    }

    private static string Show(OrchestratorWorkspace workspace, string id, DateTimeOffset? asOf = null) =>
        CliCommandTestsExperiments.Execute(["experiment-show", id, "--as-of", (asOf ?? AsOf).ToString("O")], workspace);

    private static void AssertMetric(string output, string label, double? before, double? after)
    {
        string Format(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "unavailable";
        Assert.Contains($"{label}: baseline={Format(before)} comparison={Format(after)}{Environment.NewLine}", output);
    }
}
