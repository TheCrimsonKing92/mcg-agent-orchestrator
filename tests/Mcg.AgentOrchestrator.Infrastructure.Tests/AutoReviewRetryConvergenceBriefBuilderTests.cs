using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class AutoReviewRetryConvergenceBriefBuilderTests
{
    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_SQLite_rounds_shrink_A_B_to_accept")]
    public async Task SqliteRoundsShrinkFindingsMonotonicallyToAccept()
    {
        // Parallel-safe: the real database has a per-test GUID path and is deleted in finally.
        var db = Path.Combine(Path.GetTempPath(), $"mcg-review-convergence-{Guid.NewGuid():N}.db");
        try
        {
            var repository = new SqliteOrchestratorStateRepository(db);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Structured review finding convergence");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            var anchorA = new ReviewFindingLocation("src/A.cs", "A.Run", "guard-a");
            var anchorB = new ReviewFindingLocation("src/B.cs", "B.Run", "guard-b");

            RecordReviewerRound(
                kernel,
                goal,
                reviewer,
                "needs-work",
                [
                    new ReviewFinding("F-A", ReviewFindingState.Open, anchorA, "A is missing its guard."),
                    new ReviewFinding("F-B", ReviewFindingState.Open, anchorB, "B is missing its guard.")
                ],
                []);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            var round1State = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, reviewer);
            Assert.Equal(2, ReviewFindingConvergence.CountOpen(round1State));
            var round1Brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
                goal, developer, reviewer, "A and B remain open.", "verdict=needs-work",
                AgentRole.Developer, 1, "round1.out", ["src/A.cs", "src/B.cs"]);
            Assert.Contains("open_count: 2", round1Brief);

            kernel.RetryTask(goal.Id, reviewer.Id, "round 2");
            RecordReviewerRound(
                kernel,
                goal,
                reviewer,
                "needs-work",
                [
                    new ReviewFinding("F-A", ReviewFindingState.Resolved, anchorA, "A is missing its guard."),
                    new ReviewFinding("F-B", ReviewFindingState.Open, anchorB, "B is missing its guard.")
                ],
                [anchorA]);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            var round2State = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, reviewer);
            Assert.Equal(1, ReviewFindingConvergence.CountOpen(round2State));
            var round2Brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
                goal, developer, reviewer, "Only B remains open.", "verdict=needs-work",
                AgentRole.Developer, 2, "round2.out", ["src/B.cs"]);
            var actionItems = round2Brief[..round2Brief.IndexOf("## PRESERVE_ACCEPTED", StringComparison.Ordinal)];
            Assert.Contains("stable_id: F-B", actionItems);
            Assert.DoesNotContain("stable_id: F-A", actionItems);
            Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.PreserveAcceptedDirective, round2Brief);
            Assert.Contains("stable_id: F-A", round2Brief);

            kernel.RetryTask(goal.Id, reviewer.Id, "round 3");
            RecordReviewerRound(
                kernel,
                goal,
                reviewer,
                "pass",
                [
                    new ReviewFinding("F-A", ReviewFindingState.Resolved, anchorA, "A is missing its guard."),
                    new ReviewFinding("F-B", ReviewFindingState.Resolved, anchorB, "B is missing its guard.")
                ],
                [anchorB]);
            await repository.SaveAsync(kernel);
            kernel = await repository.LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

            var round3State = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, reviewer);
            Assert.Equal(0, ReviewFindingConvergence.CountOpen(round3State));
            Assert.All(round3State, finding => Assert.Equal(ReviewFindingState.Resolved, finding.State));
            Assert.Contains("verdict: pass", reviewer.LastVerification!.StandardOutput);
            var reviewerBrief = kernel.BuildTaskBrief(
                goal.Id,
                reviewer.Id,
                reviewerScopeChangedFiles: ["src/B.cs"],
                reviewerRoundTouchedAnchors: [anchorB]);
            Assert.Contains("OPEN_ACTIVE_RECHECK count=0", reviewerBrief.Content);
            Assert.Contains("ROUND_DIFF_TOUCHED_ANCHORS", reviewerBrief.Content);
            Assert.Contains(anchorB.ToString(), reviewerBrief.Content);
            var openScope = reviewerBrief.Content[
                reviewerBrief.Content.IndexOf("OPEN_ACTIVE_RECHECK", StringComparison.Ordinal)..
                reviewerBrief.Content.IndexOf("RESOLVED_CARRIED", StringComparison.Ordinal)];
            Assert.DoesNotContain("F-A", openScope);
        }
        finally
        {
            foreach (var path in new[] { db, $"{db}-shm", $"{db}-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_deduplicates_multi_round_findings")]
    public void BuildConvergenceBriefDeduplicatesMultiRoundFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            3,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            @"C:\tmp\reviewer.out.log",
            [
                "ConductorDriverTests still expects the raw findings passthrough.",
                "Tester receipt omits ProgressiveReviewGlanceTests.",
                "  ConductorDriverTests   still expects the raw findings passthrough.  ",
                "Reviewer still needs InquiryDispatcherTests coverage."
            ],
            ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"]);

        Xunit.Assert.Contains("auto-review-retry round 3 convergence brief", brief);
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.AcceptedShapePreamble, brief);
        Xunit.Assert.Contains("Deduplicated residual blockers", brief);
        Xunit.Assert.Equal(1, CountOccurrences(brief, "- ConductorDriverTests still expects the raw findings passthrough."));
        Xunit.Assert.Contains("- Tester receipt omits ProgressiveReviewGlanceTests.", brief);
        Xunit.Assert.Contains("- Reviewer still needs InquiryDispatcherTests coverage.", brief);
        Xunit.Assert.Contains("Rerun these focused test classes at your final commit and quote receipts:", brief);
        Xunit.Assert.Contains("ConductorDriverTests", brief);
        Xunit.Assert.Contains("ProgressiveReviewGlanceTests", brief);
        Xunit.Assert.Contains("InquiryDispatcherTests", brief);
        Xunit.Assert.Contains(@"C:\tmp\reviewer.out.log", brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_keeps_single_round_findings")]
    public void BuildConvergenceBriefKeepsSingleRoundFindings()
    {
        var blocker = "Developer left tester receipt parsing unwired; retain this exact finding text.";
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            1,
            AgentRole.Tester,
            new TaskId("tester-task-0001"),
            "WORKER_RESULT blocker",
            AgentRole.Developer,
            @"C:\tmp\tester.out.log",
            [blocker],
            []);

        Xunit.Assert.Contains("auto-review-retry round 1 convergence brief", brief);
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.AcceptedShapePreamble, brief);
        Xunit.Assert.Equal(1, CountOccurrences(brief, $"- {blocker}"));
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.GenericRerunMandate, brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_keeps_all_unique_findings")]
    public void BuildConvergenceBriefKeepsAllUniqueFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            2,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            "reviewer output",
            [
                "First blocker remains open.",
                "Second blocker remains open.",
                "Third blocker remains open."
            ],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs"]);

        Xunit.Assert.Equal(1, CountOccurrences(brief, "- First blocker remains open."));
        Xunit.Assert.Equal(1, CountOccurrences(brief, "- Second blocker remains open."));
        Xunit.Assert.Equal(1, CountOccurrences(brief, "- Third blocker remains open."));
        Xunit.Assert.Contains("ConductorDriverTests", brief);
    }

    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_accumulates_verification_history")]
    public void BuildConvergenceBriefAccumulatesVerificationHistory()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Review retry convergence");
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);

        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Shared blocker stays open.");
        kernel.RetryTask(goal.Id, developer.Id, "auto-review-retry round 1 convergence brief: prior retry");
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        FailTesterBlocker(kernel, goal, tester, "  Shared   blocker stays open.  ; New tester blocker surfaced.");

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal,
            developer,
            tester,
            "Shared blocker stays open.",
            "WORKER_RESULT blocker",
            AgentRole.Developer,
            2,
            @"C:\tmp\tester.out.log",
            ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"]);

        Xunit.Assert.Equal(1, CountOccurrences(brief, "- Shared blocker stays open."));
        Xunit.Assert.Contains("- New tester blocker surfaced.", brief);
        Xunit.Assert.Contains("ConductorDriverTests", brief);
    }

    private static void DispatchTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string command = "test.exe")
    {
        var dispatch = new TaskDispatchRecord("test-worker", command, @"C:\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    }

    private static void PassVerification(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        bool hasCommittedChanges = false)
    {
        DispatchTask(kernel, goal, task);
        var verification = new TaskVerificationRecord(
            "test.exe",
            @"C:\tmp",
            0,
            "ok",
            "",
            DateTimeOffset.UtcNow,
            HasCommittedChanges: hasCommittedChanges);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static void FailReviewerNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string blocker)
    {
        DispatchTask(kernel, goal, reviewer, "review");
        var stdout = string.Join(
            Environment.NewLine,
            "Findings first.",
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            $"blockers: {blocker}",
            $"findings: {JsonSerializer.Serialize(new[] { new ReviewFinding("finding-a", ReviewFindingState.Open, new ReviewFindingLocation("src/Test.cs", "Test.Run"), blocker) })}",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "review",
            @"C:\tmp",
            1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: @"C:\tmp\reviewer.out.log",
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
    }

    private static void FailTesterBlocker(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        string blocker)
    {
        DispatchTask(kernel, goal, tester, "test");
        var stdout = string.Join(
            Environment.NewLine,
            "Tests found a correctness issue.",
            "WORKER_RESULT:",
            "files: none",
            "commands: test",
            "tests: fail - focused behavior check failed",
            $"blockers: {blocker}",
            "commit: none",
            "model_fit: test",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test",
            @"C:\tmp",
            1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: @"C:\tmp\tester.out.log",
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, verification);
    }

    private static void RecordReviewerRound(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string verdict,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<ReviewFindingLocation> touchedAnchors)
    {
        DispatchTask(kernel, goal, reviewer, "review");
        var blockers = findings.Any(finding => finding.State == ReviewFindingState.Open)
            ? string.Join("; ", findings.Where(finding => finding.State == ReviewFindingState.Open).Select(finding => finding.Description))
            : "none";
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - deterministic reviewer fixture",
            "commit: none",
            $"blockers: {blockers}",
            $"findings: {JsonSerializer.Serialize(findings)}",
            $"touched_anchors: {JsonSerializer.Serialize(touchedAnchors)}",
            $"verdict: {verdict}",
            "model_fit: fixture/model - adequate - deterministic review - test seam",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "review",
            @"C:\tmp",
            verdict == "pass" ? 0 : 1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: @"C:\tmp\reviewer.out.log",
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
