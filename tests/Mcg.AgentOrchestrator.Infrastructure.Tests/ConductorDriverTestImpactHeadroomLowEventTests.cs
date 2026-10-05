using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

// Parallel-safe: in-memory goal kernels, injected heads/dispatch, and a unique event log.
public sealed class ConductorDriverTestImpactHeadroomLowEventTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-headroom-events-");
    private string LogPath => Path.Combine(_root.FullName, "conduct-events.log");
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly RepositoryTestImpactHeadroom AtThreshold = new(3200, 4000, 77, 96, 52, 64);

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void AdvanceOnce_Thresholds_DeduplicatesAcrossGoalsUntilMainChanges()
    {
        var (kernelA, goalA) = ReadyGoal();
        var (kernelB, goalB) = ReadyGoal();
        var candidate = "candidate-a";
        var main = "main-a";
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context(candidate, AtThreshold),
            resolveAcceptanceHeads: _ => (candidate, main),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                (goalId == goalA.Id ? kernelA : kernelB).RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goalA, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goalA, ConductorAutonomyPolicy.Permissive);
        candidate = "candidate-b";
        driver.AdvanceOnce(goalA, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goalB, ConductorAutonomyPolicy.Permissive);
        var first = ReadEvents();
        Xunit.Assert.Equal(3, first.Length);
        AssertThresholdEvents(first, goalA, "candidate-a", "main-a");

        main = "main-b";
        candidate = "candidate-c";
        driver.AdvanceOnce(goalB, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(5, dispatches);
        var events = ReadEvents();
        Xunit.Assert.Equal(6, events.Length);
        AssertThresholdEvents(events.Skip(3).ToArray(), goalB, "candidate-c", "main-b");
    }

    [Xunit.Fact]
    public void AdvanceOnce_AllCountsBelowThreshold_EmitsNothingAndReviews()
    {
        var (kernel, goal) = ReadyGoal();
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context("candidate-a", new(3199, 4000, 76, 96, 51, 64)),
            resolveAcceptanceHeads: _ => ("candidate-a", "main-a"),
            recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Empty(ReadEvents());
        Xunit.Assert.Equal(1, dispatches);
        Xunit.Assert.Equal(PreReviewEvidenceDisposition.NoApplicableTests,
            goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer).PreReviewEvidenceReceipt?.Disposition);
    }

    [Xunit.Theory]
    [Xunit.InlineData(ReverseDependencyDegradationKind.IndexedSourceBound, "IndexedSourceFiles", 4001, 4000)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.SelectedTestClassBound, "SelectedTestClasses", 97, 96)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.FrontierSymbolBound, "FrontierSymbols", 65, 64)]
    public void AdvanceOnce_AbandonedBound_EmitsForCrossingWithUnreachedCountsNull(
        ReverseDependencyDegradationKind kind, string bound, int count, int cap)
    {
        var (kernel, goal) = ReadyGoal();
        var headroom = new RepositoryTestImpactHeadroom(
            kind == ReverseDependencyDegradationKind.IndexedSourceBound ? count : 3, 4000,
            kind == ReverseDependencyDegradationKind.SelectedTestClassBound ? count : null, 96,
            kind == ReverseDependencyDegradationKind.FrontierSymbolBound ? count :
                kind == ReverseDependencyDegradationKind.SelectedTestClassBound ? 1 : null, 64);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context("candidate-a", headroom, kind),
            resolveAcceptanceHeads: _ => ("candidate-a", "main-a"),
            recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt));
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var events = ReadEvents();
        var warning = Xunit.Assert.Single(events, entry =>
            entry.GetProperty("eventKind").GetString() == "test-impact-headroom-low");
        AssertEvent(warning, goal, "candidate-a", "main-a", bound, count, cap);
        Xunit.Assert.Equal(kind == ReverseDependencyDegradationKind.IndexedSourceBound ? 2 : 1, events.Length);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void AdvanceOnce_UnresolvedMain_DeduplicatesAcrossGoalsByCandidate()
    {
        var (kernelA, goalA) = ReadyGoal();
        var (kernelB, goalB) = ReadyGoal();
        var candidate = "candidate-a";
        var headroom = new RepositoryTestImpactHeadroom(8, 10, null, 96, null, 64);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context(candidate, headroom),
            resolveAcceptanceHeads: _ => (candidate, null),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                (goalId == goalA.Id ? kernelA : kernelB).RecordPreReviewEvidence(goalId, taskId, receipt));
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goalA, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goalB, ConductorAutonomyPolicy.Permissive);
        AssertEvent(Xunit.Assert.Single(ReadEvents()), goalA, candidate, "unresolved", "IndexedSourceFiles", 8, 10);
        candidate = "candidate-b";
        driver.AdvanceOnce(goalB, ConductorAutonomyPolicy.Permissive);

        var events = ReadEvents();
        Xunit.Assert.Equal(2, events.Length);
        AssertEvent(events[1], goalB, candidate, "unresolved", "IndexedSourceFiles", 8, 10);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void AdvanceOnce_MainEqualsFallbackSha_KeepsKeyNamespacesSeparate()
    {
        var (kernel, goal) = ReadyGoal();
        string? main = "same-sha";
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context("same-sha", new(8, 10, null, 96, null, 64)),
            resolveAcceptanceHeads: _ => ("same-sha", main),
            recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt));
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        main = " ";
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        main = "same-sha";
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var events = ReadEvents();
        Xunit.Assert.Equal(2, events.Length);
        AssertEvent(events[0], goal, "same-sha", "same-sha", "IndexedSourceFiles", 8, 10);
        AssertEvent(events[1], goal, "same-sha", "unresolved", "IndexedSourceFiles", 8, 10);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void AdvanceOnce_FailedAppend_RetriesWithoutBlockingReview()
    {
        var (kernel, goal) = ReadyGoal();
        var failAppend = true;
        var attempts = 0;
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context("candidate-a", new(8, 10, null, 96, null, 64)),
            resolveAcceptanceHeads: _ => ("candidate-a", "main-a"),
            recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(
            LogPath, utcNow: () => Timestamp, beforeAppendCommit: () =>
            {
                attempts++;
                if (failAppend) throw new IOException("injected log failure");
            }));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Xunit.Assert.Empty(ReadEvents());
        Xunit.Assert.Equal(1, attempts);
        Xunit.Assert.Equal(1, dispatches);
        failAppend = false;
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(2, attempts);
        Xunit.Assert.Equal(2, dispatches);
        AssertEvent(Xunit.Assert.Single(ReadEvents()), goal, "candidate-a", "main-a", "IndexedSourceFiles", 8, 10);
    }

    [Xunit.Theory]
    [Xunit.InlineData("test-impact-headroom-low", "TEST_IMPACT_HEADROOM_LOW goal=g", "decision")]
    [Xunit.InlineData("test-impact-headroom-low", "TEST_IMPACT_HEADROOM_LOW", "decision")]
    [Xunit.InlineData("test-impact-headroom-low", "TEST_IMPACT_HEADROOM_LOW_X goal=g", null)]
    [Xunit.InlineData("test-impact-headroom-low", "other TEST_IMPACT_HEADROOM_LOW goal=g", null)]
    [Xunit.InlineData("other", "TEST_IMPACT_HEADROOM_LOW goal=g", null)]
    public void Classify_KindAndExactToken_RequiresPositiveSignal(
        string eventKind, string detail, string? expected) =>
        Xunit.Assert.Equal(expected, ConductEventOperatorClassifier.Classify(eventKind, detail));

    private static PreReviewEvidenceContext Context(
        string sha, RepositoryTestImpactHeadroom headroom, ReverseDependencyDegradationKind? kind = null)
    {
        var plan = new RepositoryTestImpactPlan(true, false, "Selected infrastructure tests from changed file scope.",
            [new RepositoryTestImpactCheck("infrastructure tests", ["dotnet", "test", "--project", "infrastructure.csproj"], "fixture reason")],
            kind is { } value ? new RepositoryTestImpactDegradation(value, "fixture reason") : null,
            headroom);
        var context = ConductorDriver.BuildPreReviewEvidenceContext(sha,
            RepositoryChangeClassifier.Classify(["src/Mcg.AgentOrchestrator.Core/Application/Source.cs"]), plan);
        Xunit.Assert.Equal(headroom, context.TestImpactHeadroom);
        Xunit.Assert.True(context.NoApplicableTests);
        return context;
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) ReadyGoal()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            PassVerification(kernel, goal, task);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        return (kernel, goal);
    }

    private JsonElement[] ReadEvents() => File.Exists(LogPath)
        ? File.ReadAllLines(LogPath).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray()
        : [];

    private static void AssertThresholdEvents(JsonElement[] events, Goal goal, string candidate, string main)
    {
        AssertEvent(events[0], goal, candidate, main, "IndexedSourceFiles", 3200, 4000);
        AssertEvent(events[1], goal, candidate, main, "SelectedTestClasses", 77, 96);
        AssertEvent(events[2], goal, candidate, main, "FrontierSymbols", 52, 64);
    }

    private static void AssertEvent(JsonElement entry, Goal goal, string candidate, string main,
        string bound, int count, int cap)
    {
        Xunit.Assert.Equal("test-impact-headroom-low", entry.GetProperty("eventKind").GetString());
        Xunit.Assert.Equal(goal.Id.Value, entry.GetProperty("goalId").GetString());
        Xunit.Assert.Equal("decision", entry.GetProperty("operator").GetString());
        Xunit.Assert.Equal($"TEST_IMPACT_HEADROOM_LOW goal={goal.Id.Value[..8]} candidate={candidate} " +
            $"main={main} bound={bound} count={count} cap={cap}", entry.GetProperty("detail").GetString());
    }

    public void Dispose() => _root.Delete(recursive: true);
}
