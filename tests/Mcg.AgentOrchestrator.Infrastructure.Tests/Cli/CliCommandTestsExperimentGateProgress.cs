using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its log files and databases; clocks and console captures are explicit.
public sealed class CliCommandTestsExperimentGateProgress
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AsOf = Start.AddDays(1);

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Show_AndEvaluateCountSoloAndGroupedAttemptsAcrossRotations(int target, bool met)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Gates(target));
            var solo = Event(Start.AddHours(1), "acceptance", "ACCEPTANCE goal=solo result=passed", "solo");
            var lifecycle = Event(Start.AddHours(2), "acceptance",
                AcceptanceLifecycleEventFormatter.Format("solo", 0, "failed", "solo-attempt", 1), "solo");
            var grouped = Event(Start.AddHours(3), "acceptance-cohort",
                "ACCEPTANCE_COHORT_CHILD_COMPLETED kind=acceptance attempt=grouped members=a+b verdict=Fault");
            Write(workspace.ConductEventsLogPath, solo, lifecycle, grouped,
                Event(Start.AddHours(4), "acceptance",
                    AcceptanceLifecycleEventFormatter.Format("solo", 0, "failed", "solo-attempt", 2), "solo"),
                Event(Start.AddHours(5), "acceptance-cohort",
                    "ACCEPTANCE_COHORT_CHILD_COMPLETED kind=acceptance attempt=grouped members=a+b verdict=Fault"));
            Write(Rotated(workspace), solo, lifecycle, grouped);

            var count = ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(workspace.ConductEventsLogPath),
                ExperimentReading.ComparisonStart(record), AsOf);
            var reading = ExperimentReading.Evaluate(record, [], new Dictionary<string, DateTimeOffset>(), [], AsOf, count);
            Assert.Equal(3, reading.ObservedCount);
            Assert.Equal(met, reading.StopRuleMet);
            var output = Show(workspace, record.Id);
            Assert.Contains($"stop rule: 3 of {target} gates ({(met ? "met" : "not met")})", output);
            Assert.DoesNotContain("progress unavailable", output);
        });
    }

    [Fact]
    public void Count_IgnoresMalformedUnrelatedAndOutOfWindowEvents()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var path = workspace.ConductEventsLogPath;
            Write(path,
                Event(Start.ToOffset(TimeSpan.FromHours(-5)), "acceptance", "ACCEPTANCE result=passed", "start"),
                Event(AsOf.ToOffset(TimeSpan.FromHours(3)), "acceptance", "ACCEPTANCE result=failed", "end"),
                Event(Start.AddTicks(-1), "acceptance", "ACCEPTANCE result=passed", "before"),
                Event(AsOf.AddTicks(1), "acceptance-cohort", "ACCEPTANCE_COHORT_CHILD_COMPLETED attempt=after"),
                Event(Start.AddHours(-1), "acceptance-cohort", "ACCEPTANCE_COHORT_CHILD_COMPLETED attempt=before"),
                Event(AsOf.AddTicks(1), "acceptance", "ACCEPTANCE result=failed", "after"),
                "not JSON", "[]", "{}", "{\"timestamp\":\"invalid\",\"eventKind\":\"acceptance\",\"detail\":\"result=passed\"}",
                "{\"eventKind\":\"acceptance\",\"detail\":\"result=passed\"}",
                JsonSerializer.Serialize(new { timestamp = Start, detail = "result=passed" }),
                JsonSerializer.Serialize(new { timestamp = Start, eventKind = "acceptance", detail = 42 }));
            Write(Path.Combine(workspace.LogDirectory, "change-stream.log"),
                Event(Start, "acceptance", "ACCEPTANCE result=passed", "derived"));
            Write(Path.Combine(workspace.LogDirectory, "pending-events", "conduct-events-pending.log"),
                Event(Start, "acceptance", "ACCEPTANCE result=passed", "pending"));

            Assert.Equal(2, ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(path), Start, AsOf));
        });
    }

    [Theory]
    [InlineData("acceptance", "ACCEPTANCE result=passed", 1)]
    [InlineData("acceptance", "ACCEPTANCE result=FAILED", 1)]
    [InlineData("acceptance", "ACCEPTANCE result=started", 0)]
    [InlineData("acceptance", "ACCEPTANCE result=running", 0)]
    [InlineData("acceptance", "ACCEPTANCE result=blocked", 0)]
    [InlineData("acceptance", "ACCEPTANCE result=aborted", 0)]
    [InlineData("acceptance", "ACCEPTANCE result=fault", 0)]
    [InlineData("acceptance", "ACCEPTANCE result=failed-extra", 0)]
    [InlineData("acceptance", "ACCEPTANCE checks=result=passed", 0)]
    [InlineData("acceptance", "ACCEPTANCE result=running checks=result=passed", 0)]
    [InlineData("acceptance", "ACCEPTANCE outcome=passed", 0)]
    [InlineData("canary-gate", "ACCEPTANCE result=passed", 0)]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_CHILD_COMPLETED attempt=child verdict=failed", 1)]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_CHILD_COMPLETED attempt=child verdict=fault", 1)]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_CHILD_COMPLETED_EXTRA attempt=child", 0)]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_RECONCILED_DEAD attempt=child", 0)]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT members=a+b outcome=passed", 0)]
    public void Read_RequiresPositiveCompletionEvidence(string kind, string detail, int expected)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Write(workspace.ConductEventsLogPath, Event(Start, kind, detail));
            Assert.Equal(expected, ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(workspace.ConductEventsLogPath), Start, AsOf));
        });
    }

    [Fact]
    public void Count_FirstCompletionOwnsAttemptWindowAndDistinctRetriesCount()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Write(workspace.ConductEventsLogPath,
                Event(Start, "acceptance", "ACCEPTANCE result=passed attempt=old tick=2", "same-goal"),
                Event(Start, "acceptance", "ACCEPTANCE result=running attempt=new tick=1", "same-goal"),
                Event(Start.AddHours(1), "acceptance", "ACCEPTANCE result=passed attempt=new tick=2", "same-goal"),
                Event(Start.AddHours(2), "acceptance", "ACCEPTANCE result=passed", "same-goal"),
                Event(Start.AddHours(3), "acceptance", "ACCEPTANCE result=passed", "same-goal"));
            Write(Rotated(workspace),
                Event(Start.AddTicks(-1), "acceptance", "ACCEPTANCE result=passed attempt=old tick=1", "same-goal"));
            Assert.Equal(3, ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(workspace.ConductEventsLogPath), Start, AsOf));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Show_MissingLogOrDirectoryReportsZero(bool directoryExists)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Gates(1));
            if (directoryExists) Directory.CreateDirectory(workspace.LogDirectory);
            Assert.False(File.Exists(workspace.ConductEventsLogPath));
            Assert.Empty(ExperimentGateAttempts.Read(workspace.ConductEventsLogPath));
            Assert.Contains("stop rule: 0 of 1 gates (not met)", Show(workspace, record.Id));
        });
    }

    [Fact]
    public void Read_RotationsCountEvenWhenCurrentLogIsMissing()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Write(Rotated(workspace), Event(Start, "acceptance", "ACCEPTANCE result=passed"));
            Assert.False(File.Exists(workspace.ConductEventsLogPath));
            Assert.Equal(1, ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(workspace.ConductEventsLogPath), Start, AsOf));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Count_UsesBaselineEndOrFallsBackToCreation(bool hasBaselineEnd)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = Gates(1) with { Baseline = new(ExperimentBaselineKind.TwinGoal,
                Until: hasBaselineEnd ? Start : null, TwinGoalId: "twin") };
            var record = new ExperimentRecord("experiment", spec, Start.AddHours(1), ExperimentOutcomeState.Open, null);
            Write(workspace.ConductEventsLogPath, Event(Start, "acceptance", "ACCEPTANCE result=passed"));
            Assert.Equal(hasBaselineEnd ? Start : record.CreatedAt, ExperimentReading.ComparisonStart(record));
            var count = ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(workspace.ConductEventsLogPath),
                ExperimentReading.ComparisonStart(record), AsOf);
            Assert.Equal(hasBaselineEnd ? 1 : 0, count);
        });
    }

    [Theory]
    [InlineData(ExperimentStopUnit.Goals)]
    [InlineData(ExperimentStopUnit.Ticks)]
    public void Evaluate_GateCountLeavesOtherUnitsUnchanged(ExperimentStopUnit unit)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var kernel = CliCommandTestsExperimentReading.Seed(workspace);
            var record = CliCommandTestsExperiments.Add(workspace, Gates(2) with { StopRule = new(2, unit) });
            var reading = ExperimentReading.Evaluate(record, kernel.Goals,
                ExperimentLandingTimes.Read(workspace.GoalLifecycleEventsDirectory), [], AsOf, 5);
            var output = AsyncLocalConsoleRouter.Capture(() => ExperimentReading.Render(reading));
            if (unit == ExperimentStopUnit.Goals)
            {
                Assert.Equal(2, reading.ObservedCount);
                Assert.True(reading.StopRuleMet);
                Assert.Contains("stop rule: 2 of 2 goals (met)", output);
            }
            else
            {
                Assert.Null(reading.ObservedCount);
                Assert.False(reading.StopRuleMet);
                Assert.Contains("stop rule: unavailable of 2 ticks (progress unavailable)", output);
            }
        });
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(99, false)]
    public void ObserveTick_GatesAtTargetRaiseReadingDueOnce(int target, bool met)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var record = CliCommandTestsExperiments.Add(workspace, Gates(target));
            Write(workspace.ConductEventsLogPath, Event(Start, "acceptance", "ACCEPTANCE result=passed"));
            Write(Rotated(workspace), Event(AsOf, "acceptance-cohort",
                "ACCEPTANCE_COHORT_CHILD_COMPLETED kind=acceptance attempt=child members=a+b verdict=failed"));
            var writer = new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => AsOf);
            var watch = new ConductorExperimentWatch(workspace, writer);
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                watch.ObserveTick(AsOf);
                watch.ObserveTick(AsOf);
            });
            Assert.Empty(output);
            var events = File.ReadAllLines(workspace.ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(entry => entry.GetProperty("eventKind").GetString() == "experiment-reading-due").ToArray();
            var items = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .ListAsync().GetAwaiter().GetResult();
            if (met)
            {
                Assert.Contains($"experiment={record.Id} trigger=stop-rule observed=2 stopRuleMet=true",
                    Assert.Single(events).GetProperty("detail").GetString());
                var item = Assert.Single(items);
                Assert.Equal(CollaborationItemType.Decision, item.Type);
                Assert.Equal($"experiment-reading-due:{record.Id}:stop-rule", item.CorrelationKey);
            }
            else
            {
                Assert.Empty(events);
                Assert.Empty(items);
            }
        });
    }

    private static ExperimentSpec Gates(int target) => CliCommandTestsExperiments.Spec() with
    { StopRule = new(target, ExperimentStopUnit.Gates) };

    private static string Event(DateTimeOffset timestamp, string eventKind, string detail, string? goalId = null) =>
        JsonSerializer.Serialize(new { timestamp, eventKind, goalId, detail });

    private static void Write(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private static string Rotated(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.LogDirectory, "conduct-events-20261003000000-1.log");

    private static string Show(OrchestratorWorkspace workspace, string id) =>
        CliCommandTestsExperiments.Execute(["experiment-show", id, "--as-of", AsOf.ToString("O")], workspace);
}
