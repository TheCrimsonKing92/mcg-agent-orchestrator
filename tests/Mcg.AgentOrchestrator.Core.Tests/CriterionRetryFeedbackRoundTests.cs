using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionRetryFeedbackRoundTests
{
    private const string Earlier = "Earlier CRLF regex note";
    private const string Current = "ACTIONABLE_CANDIDATE_RED failing_tests=CurrentFailureTests.Fails";

    [Fact]
    public void LaterAutomaticRetryReplacesEarlierAuthoritativeFeedback()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Earlier, RetryCause.ContractClarification);
        CompleteFailedDispatch(kernel, goal, task, clock);
        clock.Advance();

        kernel.RetryTaskAutomatically(goal.Id, task.Id, Current, RetryCause.NewTestFinding);

        Assert.Equal([Current], task.CriterionRetryFeedback);
        Assert.Null(task.AcceptedRetryFeedback);
        var section = UnmetCriteriaSection(kernel.BuildTaskBrief(goal.Id, task.Id).Content);
        Assert.Contains("- " + Current, section, StringComparison.Ordinal);
        Assert.DoesNotContain(Earlier, section, StringComparison.Ordinal);
    }

    [Fact]
    public void SameRoundRecordedFeedbackSurvivesAutomaticRetry()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Earlier, RetryCause.ContractClarification);
        CompleteFailedDispatch(kernel, goal, task, clock);
        string[] gateEvidence = ["command: focused test", "evidence: CurrentFailureTests.Fails"];
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, gateEvidence);

        kernel.RetryTaskAutomatically(goal.Id, task.Id, Current, RetryCause.CriterionEvidenceOwnerMismatch);

        Assert.Equal(gateEvidence, task.CriterionRetryFeedback);
        var section = UnmetCriteriaSection(kernel.BuildTaskBrief(goal.Id, task.Id).Content);
        Assert.Contains(gateEvidence[0], section, StringComparison.Ordinal);
        Assert.Contains(gateEvidence[1], section, StringComparison.Ordinal);
    }

    [Fact]
    public void SameInstantReplayPreservesAuthoritativeFeedback()
    {
        var (kernel, goal, task, _) = CreateAssignedDeveloper();
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Earlier, RetryCause.ContractClarification);
        var roundAt = task.LatestRetryAt;

        // Retry admission can replay the already-applied retry with the same clock marker.
        kernel.RetryTask(goal.Id, task.Id, Earlier, RetryCause.ContractClarification);

        Assert.Equal(roundAt, task.LatestRetryAt);
        Assert.Equal([Earlier], task.CriterionRetryFeedback);
        Assert.Equal(Earlier, task.AcceptedRetryFeedback?.Message);
        Assert.Null(task.CriterionRetryFeedbackRoundAt);
    }

    [Fact]
    public void SameInstantReplayPreservesRecordedGateEvidence()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Earlier, RetryCause.ContractClarification);
        CompleteFailedDispatch(kernel, goal, task, clock);
        string[] gateEvidence = ["command: focused test", "evidence: CurrentFailureTests.Fails"];
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, gateEvidence);
        clock.Advance();
        kernel.RetryTask(goal.Id, task.Id, Current, RetryCause.CriterionEvidenceOwnerMismatch);
        var roundAt = task.LatestRetryAt;

        kernel.RetryTask(goal.Id, task.Id, Current, RetryCause.CriterionEvidenceOwnerMismatch);

        Assert.Equal(roundAt, task.LatestRetryAt);
        Assert.Equal(gateEvidence, task.CriterionRetryFeedback);
        Assert.Contains(gateEvidence[1], UnmetCriteriaSection(kernel.BuildTaskBrief(goal.Id, task.Id).Content), StringComparison.Ordinal);
    }

    [Fact]
    public void EquivalentPendingRetryKeepsAccrualBehavior()
    {
        var (kernel, goal, task, _) = CreateAssignedDeveloper();
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Earlier, RetryCause.NewSourceFinding);
        var admittedAt = task.LatestRetryAt;

        kernel.RetryTaskAutomatically(goal.Id, task.Id, Current, RetryCause.NewSourceFinding);

        Assert.Equal(admittedAt, task.LatestRetryAt);
        Assert.Equal([Earlier], task.CriterionRetryFeedback);
        Assert.Equal(Earlier, task.AcceptedRetryFeedback?.Message);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRetryFeedbackUpdated && evt.Message == Current);
    }

    [Fact]
    public void FeedbackRoundStampSurvivesSnapshotRoundTrip()
    {
        var (kernel, goal, task, clock) = CreateAssignedDeveloper();
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, Earlier, RetryCause.ContractClarification);
        var stamp = task.CriterionRetryFeedbackRoundAt;
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(kernel.ExportSnapshot()))!;
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot, clock);
        task = kernel.GetTask(goal.Id, task.Id);
        Assert.Equal(stamp, task.CriterionRetryFeedbackRoundAt);
        CompleteFailedDispatch(kernel, goal, task, clock);
        clock.Advance();

        kernel.RetryTaskAutomatically(goal.Id, task.Id, Current, RetryCause.NewTestFinding);

        Assert.Equal([Current], task.CriterionRetryFeedback);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, FakeClock Clock) CreateAssignedDeveloper()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Repair retry feedback", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (kernel, goal, goal.Tasks.Single(), clock);
    }

    private static void CompleteFailedDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, FakeClock clock)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "command", "worktree", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("command", "worktree", 1, "", "failure", clock.UtcNow));
    }

    private static string UnmetCriteriaSection(string brief)
    {
        const string heading = "## Unmet acceptance criteria from the prior attempt - fix these:";
        var start = brief.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = brief.IndexOf(Environment.NewLine + Environment.NewLine, start, StringComparison.Ordinal);
        return brief[start..(end < 0 ? brief.Length : end)];
    }
}
