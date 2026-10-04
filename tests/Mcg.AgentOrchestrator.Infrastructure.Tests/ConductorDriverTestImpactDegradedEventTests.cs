using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

// Parallel-safe: in-memory goal kernels, stub dispatch, and a unique path-scoped event writer.
public sealed class ConductorDriverTestImpactDegradedEventTests : IDisposable
{
    private const string BoundReason = "Reverse-dependency indexing exceeded the 4000-source-file bound.";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-impact-events-");
    private string LogPath => Path.Combine(_root.FullName, "conduct-events.log");
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void AdvanceOnce_IndexBound_EmitsOncePerGoalAndCandidate()
    {
        var (kernel, goal) = ReadyGoal();
        var sha = "candidate-a";
        var dispatches = 0;
        var contextBuilds = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ =>
            {
                contextBuilds++;
                return Context(sha, ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        Xunit.Assert.Equal(PreReviewEvidenceDisposition.NoApplicableTests, reviewer.PreReviewEvidenceReceipt?.Disposition);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        sha = "candidate-b";
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(3, dispatches);
        Xunit.Assert.True(contextBuilds >= 3, "Every advance must reach the evidence context path.");
        var events = ReadEvents();
        Xunit.Assert.Equal(2, events.Length);
        AssertEvent(events[0], goal, "candidate-a", ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason);
        AssertEvent(events[1], goal, "candidate-b", ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(ReverseDependencyDegradationKind.IndexedSourceBound, 1)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.Unreadable, 1)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.ChangedSourceFileCount, 0)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.FrontierSymbolBound, 0)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.AmbiguousDeclaration, 0)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.SelectedTestClassBound, 0)]
    public void AdvanceOnce_DegradationKind_EmitsOnlyOperatorWorthyEvents(
        ReverseDependencyDegradationKind kind, int expectedCount)
    {
        var (kernel, goal) = ReadyGoal();
        var dispatches = 0;
        const string reason = "reader reason";
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context("candidate-a", kind, reason),
            recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(1, dispatches);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        Xunit.Assert.Equal("candidate-a", reviewer.PreReviewEvidenceReceipt?.CandidateSha);
        var events = ReadEvents();
        Xunit.Assert.Equal(expectedCount, events.Length);
        if (expectedCount == 1) AssertEvent(events[0], goal, "candidate-a", kind, reason);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void AdvanceOnce_SameCandidateAcrossGoals_EmitsForEachGoal()
    {
        var (kernelA, goalA) = ReadyGoal();
        var (kernelB, goalB) = ReadyGoal();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context("same-sha", ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                (goalId == goalA.Id ? kernelA : kernelB).RecordPreReviewEvidence(goalId, taskId, receipt));
        driver.OverrideTestImpactDegradedEventWriterForTests(new ConductEventLogWriter(LogPath, utcNow: () => Timestamp));

        driver.AdvanceOnce(goalA, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goalB, ConductorAutonomyPolicy.Permissive);

        var events = ReadEvents();
        Xunit.Assert.Equal(2, events.Length);
        AssertEvent(events[0], goalA, "same-sha", ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason);
        AssertEvent(events[1], goalB, "same-sha", ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void AdvanceOnce_FailedEventAppend_RetriesWithoutBlockingReview()
    {
        var (kernel, goal) = ReadyGoal();
        var failAppend = true;
        var attempts = 0;
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => Context("candidate-a", ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason),
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
        AssertEvent(Xunit.Assert.Single(ReadEvents()), goal, "candidate-a",
            ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("test-impact-degraded", "TEST_IMPACT_DEGRADED goal=g", "outcome")]
    [Xunit.InlineData("test-impact-degraded", "TEST_IMPACT_DEGRADED_X goal=g", null)]
    [Xunit.InlineData("test-impact-degraded", "other TEST_IMPACT_DEGRADED goal=g", null)]
    [Xunit.InlineData("other", "TEST_IMPACT_DEGRADED goal=g", null)]
    public void Classify_EventKindAndExactToken_RequiresPositiveSignal(
        string eventKind, string detail, string? expected) =>
        Xunit.Assert.Equal(expected, ConductEventOperatorClassifier.Classify(eventKind, detail));

    private static PreReviewEvidenceContext Context(
        string sha, ReverseDependencyDegradationKind kind, string reason)
    {
        var degradation = new RepositoryTestImpactDegradation(kind, reason);
        var plan = new RepositoryTestImpactPlan(true, false, "Selected infrastructure tests from changed file scope.",
            [new RepositoryTestImpactCheck("infrastructure tests", ["dotnet", "test", "--project", "infrastructure.csproj"], reason)],
            degradation);
        var context = ConductorDriver.BuildPreReviewEvidenceContext(sha,
            RepositoryChangeClassifier.Classify(["src/Mcg.AgentOrchestrator.Core/Application/Source.cs"]), plan);
        Xunit.Assert.Equal(degradation, context.TestImpactDegradation);
        Xunit.Assert.True(context.NoApplicableTests);
        Xunit.Assert.Empty(context.SelectedFocusedTests);
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

    private static void AssertEvent(JsonElement entry, Goal goal, string sha,
        ReverseDependencyDegradationKind kind, string reason)
    {
        Xunit.Assert.Equal("test-impact-degraded", entry.GetProperty("eventKind").GetString());
        Xunit.Assert.Equal(goal.Id.Value, entry.GetProperty("goalId").GetString());
        Xunit.Assert.Equal("outcome", entry.GetProperty("operator").GetString());
        Xunit.Assert.Equal($"TEST_IMPACT_DEGRADED goal={goal.Id.Value[..8]} candidate={sha} kind={kind} reason={reason}",
            entry.GetProperty("detail").GetString());
    }

    public void Dispose() => _root.Delete(recursive: true);
}
