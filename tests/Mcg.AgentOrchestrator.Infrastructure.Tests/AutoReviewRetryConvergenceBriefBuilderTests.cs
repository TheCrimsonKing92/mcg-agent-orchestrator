using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AutoReviewRetryConvergenceBriefBuilderTests
{
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
