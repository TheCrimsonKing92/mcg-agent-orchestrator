using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its workspace, ledgers and AsyncLocal console capture.
public sealed class CliCommandTestsExperimentManualIntervention
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Until = Since.AddDays(1);
    private static readonly DateTimeOffset AsOf = Since.AddDays(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AddShowAndDecide_ManualTrialFiltersPartialRunsAndPersistsConfirmation()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            WriteLedgers(workspace, BaselineSeconds(), [900, 1000, 1050, 1100, 100]);
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            Assert.Equal(Until, record.Spec.Baseline.Until);
            Assert.Equal(ExperimentInterventionKind.Policy, record.Spec.Intervention.Kind);
            Assert.Null(record.Spec.Intervention.FlagTarget);
            Assert.Equal("6fcac5d", record.Spec.Intervention.AppliedCommit);
            Assert.Equal(300d, record.Spec.Baseline.MinGateSeconds);
            Assert.Equal(new ExperimentStopRule(4, ExperimentStopUnit.Gates), record.Spec.StopRule);

            var output = Show(workspace, record.Id);
            AssertMetric(output, "gate seconds", "1200", "1025");
            AssertMetric(output, "tick cpu ms", "200", "150");
            Assert.Contains("reading: keep (pre-registered rules evaluated)", output);
            Assert.DoesNotContain("guardrail breached", output);
            Assert.Contains($"intervention description: maxConcurrentShards 4 to 5 in the manifest{Environment.NewLine}" +
                $"intervention applied commit: 6fcac5d{Environment.NewLine}", output);
            Assert.Contains($"baseline until: {Until:O}{Environment.NewLine}" +
                $"baseline min gate seconds: 300{Environment.NewLine}", output);

            const string evidence = "receipt:manual-width-trial";
            const string action = "Keep maxConcurrentShards 5 in the manifest";
            CliCommandTestsExperiments.Execute(["experiment-decide", record.Id, "--outcome", "confirmed",
                "--evidence", evidence, "--action", action], workspace);
            var decided = new ExperimentStore(workspace.ExperimentStorePath).ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            Assert.Equal(ExperimentOutcomeState.Confirmed, decided.Outcome);
            Assert.NotNull(decided.Decision);
            Assert.Equal(ExperimentOutcomeState.Confirmed, decided.Decision.Outcome);
            Assert.Equal(evidence, decided.Decision.Evidence);
            Assert.Equal(action, decided.Decision.Action);
        });
    }

    [Theory]
    [InlineData(true, "1475", "revert")]
    [InlineData(false, "1200", "inconclusive")]
    public void Show_FilteredTrialEvaluatesRevertAndInconclusive(bool slower, string median, string verdict)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            double[] comparison = slower ? [1400, 1450, 1500, 1550, 100] : [1150, 1200, 1250];
            WriteLedgers(workspace, BaselineSeconds(), comparison);
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var output = Show(workspace, record.Id);

            AssertMetric(output, "gate seconds", "1200", median);
            AssertMetric(output, "tick cpu ms", "200", "150");
            Assert.Contains($"reading: {verdict} (pre-registered rules evaluated)", output);
        });
    }

    [Fact]
    public void Show_WithoutOptionalFieldsKeepsUnfilteredMediansAndOriginalOutput()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            WriteLedgers(workspace, BaselineSeconds(), [900, 1000, 1050, 1100, 100]);
            var record = CliCommandTestsExperiments.Add(workspace, Spec(null, null));
            var output = Show(workspace, record.Id);

            // Same samples as the filtered trial: medians of all completed rows are 600 and 1000.
            AssertMetric(output, "gate seconds", "600", "1000");
            AssertMetric(output, "tick cpu ms", "200", "150");
            Assert.Contains("reading: revert (pre-registered rules evaluated)", output);
            Assert.DoesNotContain("baseline min gate seconds", output);
            Assert.DoesNotContain("intervention applied commit", output);
        });
    }

    [Fact]
    public void Show_MinimumIncludesExactBoundaryAndExcludesFractionBelowItInBothWindows()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            WriteLedgers(workspace, [299.9, 300], [299.9, 300]);
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var output = Show(workspace, record.Id);

            AssertMetric(output, "gate seconds", "300", "300");
            Assert.Contains("reading: inconclusive (pre-registered rules evaluated)", output);
        });
    }

    [Fact]
    public void Show_MinimumAboveEveryRowReportsUnavailableAndInconclusive()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            WriteLedgers(workspace, BaselineSeconds(), [900, 1000, 1050, 1100, 100]);
            var record = CliCommandTestsExperiments.Add(workspace, Spec(2000));
            var output = Show(workspace, record.Id);

            AssertMetric(output, "gate seconds", "unavailable", "unavailable");
            AssertMetric(output, "tick cpu ms", "200", "150");
            Assert.Contains("reading: inconclusive (unavailable comparison or zero baseline: gate-seconds)", output);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Add_RejectsNonPositiveMinimumWithoutStoringExperiment(double minimum)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
            AssertRejected(workspace, Spec(minimum), "baseline.minGateSeconds:"));
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.TwinGoal)]
    [InlineData(ExperimentBaselineKind.AlternatingGates)]
    [InlineData(ExperimentBaselineKind.GoalCohort)]
    public void Add_RejectsMinimumOnOtherBaselineKindsWithoutStoringExperiment(ExperimentBaselineKind kind)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = Spec();
            spec = spec with
            {
                Baseline = spec.Baseline with
                {
                    Kind = kind, TwinGoalId = "baseline", ComparisonGoalId = "comparison",
                    BaselineGoalIds = ["baseline"], ComparisonGoalIds = ["comparison"]
                }
            };
            AssertRejected(workspace, spec, "baseline.minGateSeconds:");
        });
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("abc12")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("6fcac5d ")]
    [InlineData("6fcac5d\n")]
    public void Add_RejectsInvalidCommitWithoutStoringExperiment(string commit)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
            AssertRejected(workspace, Spec(commit: commit), "intervention.appliedCommit:"));
    }

    [Theory]
    [InlineData("6FCAC5D")]
    [InlineData("0123456789abcdef0123456789ABCDEF01234567")]
    public void AddAndShow_CommitLabelRoundTripsVerbatim(string commit)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Spec(commit: commit));
            Assert.Equal(commit, record.Spec.Intervention.AppliedCommit);
            Assert.Contains($"intervention applied commit: {commit}{Environment.NewLine}", Show(workspace, record.Id));
        });
    }

    [Fact]
    public void Serialize_AbsentOptionalFieldsAreOmittedFromStoredForm()
    {
        var spec = Spec(null, null);
        var json = JsonSerializer.Serialize(spec, ExperimentStore.JsonOptions);

        Assert.DoesNotContain("minGateSeconds", json);
        Assert.DoesNotContain("appliedCommit", json);
        var restored = JsonSerializer.Deserialize<ExperimentSpec>(json, ExperimentStore.JsonOptions)!;
        Assert.Null(restored.Baseline.MinGateSeconds);
        Assert.Null(restored.Intervention.AppliedCommit);
        Assert.Equal(json, JsonSerializer.Serialize(restored, ExperimentStore.JsonOptions));
    }

    private static ExperimentSpec Spec(double? minimum = 300, string? commit = "6fcac5d") => new(
        "Raising manifest gate width reduces full-run gate seconds",
        new(ExperimentInterventionKind.Policy, "maxConcurrentShards 4 to 5 in the manifest", AppliedCommit: commit),
        new(ExperimentBaselineKind.BeforeAfterWindow, Since, Until, MinGateSeconds: minimum),
        ["gate-seconds"], new("tick-cpu-ms", new("tick-cpu-ms", ">", 50)),
        new(4, ExperimentStopUnit.Gates),
        new([new("gate-seconds", "<=", -5)], [new("gate-seconds", ">=", 15)]));

    private static double[] BaselineSeconds() => [1100, 1200, 1300, 100, 100, 100];

    private static void AssertRejected(OrchestratorWorkspace workspace, ExperimentSpec spec, string field)
    {
        var error = Assert.Throws<ArgumentException>(() => CliCommandTestsExperiments.Add(workspace, spec));
        Assert.StartsWith(field, error.Message);
        Assert.Equal(0, new ExperimentStore(workspace.ExperimentStorePath).CountAsync().GetAwaiter().GetResult());
    }

    private static void WriteLedgers(OrchestratorWorkspace workspace,
        IReadOnlyList<double> baseline, IReadOnlyList<double> comparison)
    {
        var gates = baseline.Select((seconds, index) => Gate(Since.AddHours(index), seconds))
            .Concat(comparison.Select((seconds, index) => Gate(Until.AddHours(index), seconds))).ToArray();
        Write(GateLaneTimeLedger.ResolveStorePath(workspace.ExecutionDirectory), gates);
        Write(TickCpuLedger.ResolveStorePath(workspace.ExecutionDirectory),
            new[] { Tick(Since, 100), Tick(Since.AddHours(1), 300), Tick(Until, 150) });
    }

    private static GateLaneTimeLedgerRecord Gate(DateTimeOffset at, double seconds) =>
        new(at, "fixture-attempt", "fixture-goal", "completed", seconds * 1000, seconds * 1000, 0, 0);

    private static TickCpuLedgerRecord Tick(DateTimeOffset at, long cpuMs) =>
        new(at, 1, cpuMs, cpuMs, 0, 0, false);

    private static void Write<T>(string path, IReadOnlyList<T> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
    }

    private static string Show(OrchestratorWorkspace workspace, string id) =>
        CliCommandTestsExperiments.Execute(["experiment-show", id, "--as-of", AsOf.ToString("O")], workspace);

    private static void AssertMetric(string output, string label, string baseline, string comparison) =>
        Assert.Contains($"{label}: baseline={baseline} comparison={comparison}{Environment.NewLine}", output);
}
