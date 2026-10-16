using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static ConductorDriverTests;
using static Mcg.AgentOrchestrator.App.Orchestration.TestFirstScopeCheck;

[Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsTestFirstScopeCheck
{
    [Fact]
    public void AuthorViolation_RecordsFailureAndRetriesAuthorWithoutDispatchingImplementer()
    {
        var scenario = RunScenario(violation: true);
        var retry = Assert.Single(scenario.Retries);
        Assert.Equal(scenario.AuthorId, retry.TaskId);
        Assert.Equal(RetryRoundKind.Mechanical, retry.Kind);
        Assert.Equal(RetryCause.NewSourceFinding, retry.Cause);
        Assert.Equal([scenario.AuthorId], scenario.Dispatched);
        Assert.DoesNotContain(scenario.ImplementerId, scenario.Dispatched);
        Assert.StartsWith("test-first scope check failed:", Assert.Single(scenario.Notes).Message, StringComparison.Ordinal);
        Assert.Equal(scenario.AuthorId, scenario.Notes[0].TaskId);
        Assert.Equal(1, scenario.ReaderCalls);
    }

    [Fact]
    public void CleanAuthor_RecordsPassAndDispatchesImplementer()
    {
        var scenario = RunScenario(violation: false);
        Assert.Empty(scenario.Retries);
        Assert.Equal([scenario.ImplementerId], scenario.Dispatched);
        Assert.StartsWith("test-first scope check passed:", Assert.Single(scenario.Notes).Message, StringComparison.Ordinal);
        Assert.Equal(scenario.AuthorId, scenario.Notes[0].TaskId);
        Assert.Equal(1, scenario.ReaderCalls);
    }

    [Fact]
    public void RepeatedViolation_EscalatesWithDecisionAndDoesNotRetry()
    {
        var scenario = RunScenario(violation: true, repeat: true);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Dispatched);
        var escalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Result.Outcome);
        Assert.StartsWith("TEST_FIRST_SCOPE_REPEATED", escalation.Reason, StringComparison.Ordinal);
        Assert.NotNull(escalation.Decision);
        Assert.Equal("test-first-scope", escalation.Decision.Stage);
        Assert.StartsWith("test-first scope check failed:", Assert.Single(scenario.Notes).Message, StringComparison.Ordinal);
        Assert.Equal(1, scenario.ReaderCalls);
    }

    [Fact]
    public void ScoutDecision_ProducesNoScopeNoteAndNeverReadsGit()
    {
        var scenario = RunScenario(violation: true, testFirst: false);
        Assert.Empty(scenario.Notes);
        Assert.Empty(scenario.Retries);
        Assert.Equal(0, scenario.ReaderCalls);
        Assert.Equal([scenario.ImplementerId], scenario.Dispatched);
    }

    [Fact]
    public void MissingAuthorCommit_RecordsSkipAndDispatchesImplementer()
    {
        var scenario = RunScenario(violation: false, skip: true);
        Assert.Empty(scenario.Retries);
        Assert.Equal([scenario.ImplementerId], scenario.Dispatched);
        Assert.Equal("test-first scope check skipped: no author commit", Assert.Single(scenario.Notes).Message);
    }

    [Fact]
    public void ImplementerViolation_RetriesImplementerInsteadOfAuthor()
    {
        var scenario = RunScenario(violation: true, implementerCompleted: true);
        var retry = Assert.Single(scenario.Retries);
        Assert.Equal(scenario.ImplementerId, retry.TaskId);
        Assert.Equal(RetryRoundKind.Mechanical, retry.Kind);
        Assert.Equal(RetryCause.NewSourceFinding, retry.Cause);
        Assert.Equal([scenario.ImplementerId], scenario.Dispatched);
        Assert.Equal(scenario.ImplementerId, Assert.Single(scenario.Notes).TaskId);
    }

    private static Scenario RunScenario(bool violation, bool repeat = false, bool testFirst = true,
        bool skip = false, bool implementerCompleted = false)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, DefaultAgents(),
                GoalObjectivePlanner.Build("Implement src/Widget.cs with focused tests.", GoalIntakePipeline.TestFirst));
            if (!testFirst) kernel.RecordGoalPolicyDecision(goal.Id, "Intake pipeline decision (override): scout; test fixture.");
            var author = goal.Tasks[1];
            var implementer = goal.Tasks[2];
            PassVerification(kernel, goal, goal.Tasks[0]);
            PassVerification(kernel, goal, author);
            if (implementerCompleted) PassVerification(kernel, goal, implementer);
            if (repeat) kernel.RecordTaskNote(goal.Id, author.Id, "test-first scope check failed: previous violation");
            var notes = new List<(TaskId TaskId, string Message)>();
            var retries = new List<(TaskId TaskId, RetryRoundKind? Kind, RetryCause Cause)>();
            var dispatched = new List<TaskId>();
            var readerCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    dispatched.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).Id);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, kind, cause) =>
                {
                    retries.Add((taskId, kind, cause));
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: kind, retryCause: cause);
                },
                recordTaskNote: (goalId, taskId, message) =>
                {
                    if (message.StartsWith("test-first scope check", StringComparison.Ordinal)) notes.Add((taskId, message));
                    kernel.RecordTaskNote(goalId, taskId, message);
                },
                executionDirectory: root);
            driver.OverrideTestFirstScopeReaderForTests((path, branch, taskId) =>
            {
                readerCalls++;
                Assert.True(testFirst, "Non-test-first goal invoked the scope reader.");
                Assert.Equal(GoalWorktrees.WorktreePath(root, goal.Id), path);
                Assert.False(string.IsNullOrWhiteSpace(branch));
                Assert.Equal(author.Id.Value, taskId);
                return skip ? TestFirstScopeResult.Skip("no author commit") : Evaluate(CheckpointKind.Author,
                    violation && !implementerCompleted ? [(1, 0, "docs/widget.md")] : [(12, 0, "tests/WidgetCases.cs")],
                    violation && implementerCompleted ? ["tests/WidgetCases.cs"] : []);
            });

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            return new(author.Id, implementer.Id, result, notes, retries, dispatched, readerCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Scenario(TaskId AuthorId, TaskId ImplementerId, ConductorAdvanceResult Result,
        List<(TaskId TaskId, string Message)> Notes,
        List<(TaskId TaskId, RetryRoundKind? Kind, RetryCause Cause)> Retries,
        List<TaskId> Dispatched, int ReaderCalls);
}
