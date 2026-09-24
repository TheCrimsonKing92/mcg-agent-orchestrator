using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorDriverTestsPreReviewRepeatedFailingSet
{
    private static readonly string[] RepeatedTests = ["Example.Tests.First", "Example.Tests.Second"];

    [Xunit.Fact]
    public void SecondIdenticalRedRoundPutsCurrentAssertionsAndRepositoryFramesInDeveloperBrief()
    {
        var trxPath = WriteResultFile();
        try
        {
            var run = Run([Receipt("sha-1", RepeatedTests)], "sha-2", RepeatedTests, trxPath);

            Assert.Single(run.RetriedTasks);
            Assert.Null(run.Escalation);
            var brief = run.Kernel.BuildTaskBrief(run.Goal.Id, run.Developer.Id).Content;
            Assert.Contains("2 consecutive focused pre-review evidence rounds", brief, StringComparison.Ordinal);
            Assert.Contains("Example.Tests.First", brief, StringComparison.Ordinal);
            Assert.Contains("Example.Tests.Second", brief, StringComparison.Ordinal);
            Assert.Contains("Expected: Completed Actual: Verified", brief, StringComparison.Ordinal);
            Assert.Contains("tests\\Example.cs:line 42", brief, StringComparison.Ordinal);
            Assert.Contains("Expected: Open Actual: Closed", brief, StringComparison.Ordinal);
            Assert.Contains("src\\Example.cs:line 18", brief, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Xunit.Fact]
    public void ThirdIdenticalRedRoundHoldsWithoutDeveloperDispatch()
    {
        var run = Run([Receipt("sha-1", RepeatedTests), Receipt("sha-2", RepeatedTests)],
            "sha-3", RepeatedTests);

        Assert.Empty(run.RetriedTasks);
        Assert.Equal(WorkTaskStatus.Completed, run.Developer.Status);
        Assert.Contains("PRE_REVIEW_REPEATED_FAILING_SET", run.Escalation, StringComparison.Ordinal);
        Assert.Contains("rounds=3", run.Escalation, StringComparison.Ordinal);
        Assert.Contains("Example.Tests.First", run.Escalation, StringComparison.Ordinal);
        Assert.Contains("Example.Tests.Second", run.Escalation, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ChangedFailingIdentityRetriesDeveloperWithoutRepeatStatement()
    {
        var run = Run([Receipt("sha-1", RepeatedTests)], "sha-2",
            ["Example.Tests.First", "Example.Tests.Third"]);

        Assert.Single(run.RetriedTasks);
        Assert.Null(run.Escalation);
        var brief = run.Kernel.BuildTaskBrief(run.Goal.Id, run.Developer.Id).Content;
        Assert.DoesNotContain("PRE_REVIEW_REPEATED_FAILURE_START", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("consecutive focused pre-review evidence rounds", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OperatorDeveloperRetryClearsStaleHoldForLaterTesterFinding()
    {
        var (kernel, goal) = ConductorDriverTests.SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        ConductorDriverTests.PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        ConductorDriverTests.PassVerification(kernel, goal, tester);
        var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var sha in new[] { "sha-1", "sha-2", "sha-3" })
            kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id,
                Receipt(sha, RepeatedTests) with { GoalId = goal.Id.Value, RecordedAt = recordedAt });

        kernel.RetryTask(goal.Id, tester.Id, "Check the current finding.");
        ConductorDriverTests.FailTesterBlocker(kernel, goal, tester, "Developer repair needed.");
        var retried = new List<TaskId>();
        var escalations = new List<string>();
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retried.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalations.Add(message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Empty(retried);
        Assert.Contains(escalations, message => message.Contains("PRE_REVIEW_REPEATED_FAILING_SET", StringComparison.Ordinal));

        kernel.RetryTask(goal.Id, developer.Id, "Operator guidance for another attempt", invalidateDownstream: false);
        ConductorDriverTests.PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RetryTask(goal.Id, tester.Id, "Check the new Developer result.");
        ConductorDriverTests.FailTesterBlocker(kernel, goal, tester, "Developer repair still needed.");
        escalations.Clear();

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal([developer.Id], retried);
        Assert.DoesNotContain(escalations, message => message.Contains("PRE_REVIEW_REPEATED_FAILING_SET", StringComparison.Ordinal));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer,
        List<TaskId> RetriedTasks, string? Escalation) Run(
        IReadOnlyList<PreReviewEvidenceReceipt> prior,
        string currentSha,
        string[] currentTests,
        string? trxPath = null)
    {
        var (kernel, goal) = ConductorDriverTests.SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            ConductorDriverTests.PassVerification(kernel, goal, task);
        foreach (var receipt in prior)
            kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, receipt with { GoalId = goal.Id.Value });

        var context = ConductorDriverTests.FocusedPreReviewContext(currentSha);
        var retried = new List<TaskId>();
        string? escalation = null;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => context,
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request, Accepted: true, Passed: false, Summary: "focused tests failed",
                Checks: [new AcceptanceCheckResult("focused tests", false, 1, "failure",
                    TestResultPaths: trxPath is null ? [] : [trxPath], FailingTestIdentities: currentTests)]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retried.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        return (kernel, goal, developer, retried, escalation);
    }

    private static PreReviewEvidenceReceipt Receipt(string sha, string[] names) =>
        new("goal", 1, sha, [], PreReviewEvidenceDisposition.Red, 0, 1,
            [new PreReviewEvidenceCheckReceipt("focused tests", "test", false, 1)],
            names, "mapped", null, DateTimeOffset.UtcNow);

    private static string WriteResultFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repeated-failures-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, """
            <TestRun><Results>
              <UnitTestResult testName="Example.Tests.First" outcome="Failed"><Output><ErrorInfo>
                <Message>Expected: Completed&#10;Actual: Verified</Message>
                <StackTrace>at Xunit.Framework()&#10;at Example.Tests.First() in C:\repo\tests\Example.cs:line 42</StackTrace>
              </ErrorInfo></Output></UnitTestResult>
              <UnitTestResult testName="Example.Tests.Second" outcome="Failed"><Output><ErrorInfo>
                <Message>Expected: Open&#10;Actual: Closed</Message>
                <StackTrace>at Example.Tests.Second() in C:\repo\src\Example.cs:line 18</StackTrace>
              </ErrorInfo></Output></UnitTestResult>
            </Results></TestRun>
            """);
        return path;
    }
}
