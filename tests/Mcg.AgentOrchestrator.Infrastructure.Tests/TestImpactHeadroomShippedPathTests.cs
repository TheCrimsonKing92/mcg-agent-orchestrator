using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;
using RepositoryFixture = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: unique fixture/log roots, independent kernels, injected heads/dispatch and fixed clocks.
public sealed class TestImpactHeadroomShippedPathTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void AdvanceOnce_AppOnlyAtThreshold_WarnsOncePerMainAcrossGoals()
    {
        using var scenario = new Scenario(8);
        Assert.Null(scenario.Context().TestImpactHeadroom);

        scenario.Tick(0);
        scenario.Tick(0);
        scenario.Tick(1);
        Assert.Equal(1, scenario.Warning.MeasurementCount);
        Assert.Equal(3, scenario.Dispatches);
        AssertWarning(Assert.Single(scenario.Lines()), scenario.Goals[0], "main-a");

        scenario.Main = "main-b";
        scenario.Tick(1);

        var lines = scenario.Lines();
        Assert.Equal(2, lines.Length);
        AssertWarning(lines[0], scenario.Goals[0], "main-a");
        AssertWarning(lines[1], scenario.Goals[1], "main-b");
        Assert.Equal(2, scenario.Warning.MeasurementCount);
        Assert.Equal(4, scenario.Dispatches);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void AdvanceOnce_BelowThreshold_MatchesBaselineWithoutWarning()
    {
        using var scenario = new Scenario(7);
        using var baseline = new Scenario(7, measure: false);
        Assert.Null(scenario.Context().TestImpactHeadroom);
        CompareThreeTicks(scenario, baseline);
        Assert.Equal(1, scenario.Warning.MeasurementCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void AdvanceOnce_MissingProject_CachesNullAndMatchesBaseline()
    {
        using var scenario = new Scenario(8, missingProject: true);
        using var baseline = new Scenario(8, measure: false, missingProject: true);
        Assert.Null(RepositoryTestImpactClosure.MeasureIndexedSources(scenario.Fixture.Root, 10));
        CompareThreeTicks(scenario, baseline);
        Assert.Equal(1, scenario.Warning.MeasurementCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void AdvanceOnce_PlannedCount_WinsWithoutFallbackAndMatchesBaseline()
    {
        using var scenario = new Scenario(8, coreCandidate: true);
        using var baseline = new Scenario(8, measure: false, coreCandidate: true);
        var headroom = scenario.Context().TestImpactHeadroom;
        Assert.NotNull(headroom);
        Assert.Equal(9, headroom.IndexedSourceFileCount);
        Assert.Equal(RepositoryTestImpactClosure.DefaultIndexedSourceFileCap, headroom.IndexedSourceFileCap);
        CompareThreeTicks(scenario, baseline);
        Assert.Equal(0, scenario.Warning.MeasurementCount);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void AdvanceOnce_AppendFailsOnce_RetriesWithoutMeasuringOrBlocking()
    {
        using var scenario = new Scenario(8, failFirstAppend: true);
        using var baseline = new Scenario(8, measure: false);
        scenario.Tick(0);
        baseline.Tick(0);
        Assert.Empty(scenario.Lines());
        Assert.Equal(baseline.Lines(), scenario.Lines());
        Assert.Equal(1, scenario.AppendAttempts);
        scenario.AssertDispatchMatches(baseline);

        scenario.Tick(0);
        baseline.Tick(0);
        AssertWarning(Assert.Single(scenario.Lines()), scenario.Goals[0], "main-a");
        Assert.Empty(baseline.Lines());
        Assert.Equal(2, scenario.AppendAttempts);
        Assert.Equal(1, scenario.Warning.MeasurementCount);
        scenario.AssertDispatchMatches(baseline);
    }

    private static void CompareThreeTicks(Scenario scenario, Scenario baseline)
    {
        foreach (var goalIndex in new[] { 0, 0, 1 })
        {
            scenario.Tick(goalIndex);
            baseline.Tick(goalIndex);
            scenario.AssertDispatchMatches(baseline);
            Assert.Equal(baseline.Lines(), scenario.Lines());
        }
        Assert.Empty(scenario.Lines());
        Assert.Empty(baseline.Lines());
        Assert.Equal(0, baseline.Warning.MeasurementCount);
    }

    private static void AssertWarning(string line, Goal goal, string main)
    {
        using var document = JsonDocument.Parse(line);
        var entry = document.RootElement;
        Assert.Equal("test-impact-headroom-low", entry.GetProperty("eventKind").GetString());
        Assert.Equal(goal.Id.Value, entry.GetProperty("goalId").GetString());
        Assert.Equal("decision", entry.GetProperty("operator").GetString());
        Assert.Equal($"TEST_IMPACT_HEADROOM_LOW goal={goal.Id.Value[..8]} candidate={Scenario.Candidate} " +
            $"main={main} bound=IndexedSourceFiles count=8 cap=10", entry.GetProperty("detail").GetString());
    }

    private sealed class Scenario : IDisposable
    {
        internal const string Candidate = "candidate-a";
        private const string CoreSource = "src/Mcg.AgentOrchestrator.Core/Application/HeadroomProbe.cs";
        internal RepositoryFixture Fixture { get; } = new();
        internal Goal[] Goals { get; }
        internal TestImpactHeadroomWarning Warning { get; }
        internal string Main { get; set; } = "main-a";
        internal int Dispatches { get; private set; }
        internal int AppendAttempts { get; private set; }
        private readonly string[] _changedFiles;
        private readonly ConductorDriver _driver;
        private readonly List<(Type Outcome, PreReviewEvidenceDisposition? Disposition)> _ticks = [];
        private string LogPath => Path.Combine(Fixture.Root, "conduct-events.log");

        internal Scenario(int closureSize, bool measure = true, bool missingProject = false,
            bool coreCandidate = false, bool failFirstAppend = false)
        {
            for (var index = 2; index < closureSize; index++)
                Fixture.Write($"src/Mcg.AgentOrchestrator.App/Extra{index}.cs", $"public class Extra{index} {{ }}");
            Assert.Equal(closureSize,
                RepositoryTestImpactClosure.MeasureIndexedSources(Fixture.Root, 10)!.IndexedSourceFileCount);
            if (missingProject)
                File.Delete(Path.Combine(Fixture.Root, RepositoryFixture.TestDirectory,
                    "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
            if (coreCandidate) Fixture.Write(CoreSource, "public class HeadroomProbe { }");
            _changedFiles = [coreCandidate ? CoreSource : RepositoryFixture.Other];
            var (kernelA, goalA) = ReadyGoal();
            var (kernelB, goalB) = ReadyGoal();
            Goals = [goalA, goalB];
            Assert.True(Context().NoApplicableTests);
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => ConductorDriver.BuildPreReviewEvidenceContext(
                    Candidate, _changedFiles, Fixture.Root),
                resolveAcceptanceHeads: _ => (Candidate, Main),
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    (goalId == goalA.Id ? kernelA : kernelB).RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ => { Dispatches++; return DispatchStartOutcome.Started(); });
            Warning = new(() => measure ? Fixture.Root : null, 10);
            _driver.HeadroomWarning = Warning;
            _driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath,
                utcNow: () => DateTimeOffset.UnixEpoch, beforeAppendCommit: () =>
                {
                    AppendAttempts++;
                    if (failFirstAppend && AppendAttempts == 1) throw new IOException("injected append failure");
                }));
        }

        internal PreReviewEvidenceContext Context() =>
            ConductorDriver.BuildPreReviewEvidenceContext(Candidate, _changedFiles, Fixture.Root);

        internal void Tick(int goalIndex)
        {
            var goal = Goals[goalIndex];
            var before = Dispatches;
            var result = _driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            Assert.Equal(before + 1, Dispatches);
            var receipt = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer).PreReviewEvidenceReceipt;
            Assert.NotNull(receipt);
            Assert.Equal(PreReviewEvidenceDisposition.NoApplicableTests, receipt.Disposition);
            _ticks.Add((result.Outcome.GetType(), receipt.Disposition));
        }

        internal void AssertDispatchMatches(Scenario baseline)
        {
            Assert.Equal(baseline.Dispatches, Dispatches);
            Assert.Equal(baseline._ticks, _ticks);
        }

        internal string[] Lines() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

        private static (AgentOrchestratorKernel Kernel, Goal Goal) ReadyGoal()
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
                PassVerification(kernel, goal, task);
            Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
            return (kernel, goal);
        }

        public void Dispose() => Fixture.Dispose();
    }
}
