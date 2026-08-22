using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerDispatchBuildEvidenceClassificationTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void CompiledChangeWithoutBuildEvidenceIsRejectedAndNamesProject(AgentRole role)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
            root,
            role,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", "deferred - acceptance gate owns tests"),
            string.Empty,
            clock,
            AddCompiledFeature);
        var acceptanceRetriesBefore = goal.AutomaticAcceptanceRetryCount;
        var reviewRoundBefore = ReviewRetryCapReceipt.Create(goal, 2).Round;

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        Xunit.Assert.Contains("src/Feature/Feature.csproj", task.LastVerification.StandardError, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification);
        Xunit.Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
        Xunit.Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Xunit.Assert.Contains("rule=worker-build-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Xunit.Assert.Equal(acceptanceRetriesBefore, goal.AutomaticAcceptanceRetryCount);
        Xunit.Assert.Equal(reviewRoundBefore, ReviewRetryCapReceipt.Create(goal, 2).Round);
    }

    [Xunit.Fact]
    public void CompiledChangeWithBuildEvidenceCompletesAndCommits()
    {
        var (task, worktree) = RefreshDeveloper(
            "deferred - Invoke-WorkerBuildCheck passed with 0 errors; acceptance gate owns tests",
            AddCompiledFeature);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    }

    [Xunit.Fact]
    public void NonCompiledChangeWithoutBuildEvidenceKeepsExistingCompletion()
    {
        var (task, worktree) = RefreshDeveloper(
            "deferred - acceptance gate owns tests",
            worktree => File.WriteAllText(Path.Combine(worktree, "README.md"), "documentation"));

        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    }

    [Xunit.Fact]
    public void ExplicitBuildFailureKeepsExistingTypedReason()
    {
        var (task, _) = RefreshDeveloper(
            "fail - build: 1 error (Invoke-WorkerBuildCheck)",
            AddCompiledFeature);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
            task.LastVerification!.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(
            DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing,
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReviewerDeferredResultIsUnaffected()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var reviewerResult = WorkerResultBlock(
            "none",
            "reviewed candidate",
            "deferred - acceptance gate owns tests")
            .Replace(
                "END_WORKER_RESULT",
                "findings: []\n" +
                "touched_anchors: []\n" +
                "criteria_verdicts: []\n" +
                "verdict: pass\n" +
                "END_WORKER_RESULT",
                StringComparison.Ordinal);
        var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Reviewer,
            reviewerResult,
            string.Empty,
            clock);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
    }

    private static (TaskSpec Task, string Worktree) RefreshDeveloper(
        string tests,
        Action<string> mutateWorktree)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Developer,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", tests),
            string.Empty,
            clock,
            mutateWorktree);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);
        return (task, process.WorkingDirectory);
    }

    private static void AddCompiledFeature(string worktree)
    {
        var directory = Path.Combine(worktree, "src", "Feature");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Feature.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(directory, "Feature.cs"), "public sealed class Feature { }");
    }
}
