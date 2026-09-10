using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class PendingRetryLifecycleTests
{
    [Xunit.Theory]
    [Xunit.InlineData(PaidRouteClassification.Paid)]
    [Xunit.InlineData(PaidRouteClassification.NonPaid)]
    [Xunit.InlineData(PaidRouteClassification.Unknown)]
    public void PreparedDispatchAdmissionMetadataSurvivesSnapshotRoundTrip(PaidRouteClassification route)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve prepared admission", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RetryTask(goal.Id, task.Id, "Retry with current evidence", RetryCause.NewSourceFinding);
        var fingerprint = new RetryContextFingerprint(1, "prepared-admission-context");
        var dispatch = new TaskDispatchRecord("worker", "command", "worktree", DateTimeOffset.UtcNow,
            RetryContextFingerprint: fingerprint, PaidRoute: route);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var snapshot = kernel.ExportSnapshot();
        var saved = snapshot.Goals.Single().Tasks.Single();
        Assert.Equal(fingerprint, saved.LastDispatch!.RetryContextFingerprint);
        Assert.Equal(route, saved.LastDispatch.PaidRoute);
        Assert.Equal(saved.LastDispatch, Assert.Single(saved.DispatchHistory!));
        var restored = AgentOrchestratorKernel.FromSnapshot(
            JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(snapshot))!);
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(fingerprint, restoredTask.LastDispatch!.RetryContextFingerprint);
        Assert.Equal(route, restoredTask.LastDispatch.PaidRoute);
        Assert.Equal(restoredTask.LastDispatch, Assert.Single(restoredTask.DispatchHistory));
        Assert.Equal(dispatch.DispatchedAt, restoredTask.LastDispatch.DispatchedAt);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    public void LegacyAdmissionMetadataRecoveryRequiresMatchingIdentityAndMissingFields(bool sameIdentity, bool missingFields)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Restore exact prepared admission", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var original = new RetryContextFingerprint(1, "history-context");
        var current = new RetryContextFingerprint(1, "current-context");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "command", "worktree", DateTimeOffset.UtcNow,
            RetryContextFingerprint: original, PaidRoute: PaidRouteClassification.Paid));
        var snapshot = kernel.ExportSnapshot();
        var savedGoal = snapshot.Goals.Single();
        var savedTask = savedGoal.Tasks.Single();
        var legacyCurrent = savedTask.LastDispatch! with
        {
            Command = sameIdentity ? "command" : "different-command",
            RetryContextFingerprint = missingFields ? null : current,
            PaidRoute = missingFields ? PaidRouteClassification.Unknown : PaidRouteClassification.NonPaid
        };
        snapshot = snapshot with { Goals = [savedGoal with { Tasks = [savedTask with { LastDispatch = legacyCurrent }] }] };

        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot).GetTask(goal.Id, task.Id);
        var shouldRecover = sameIdentity && missingFields;
        Assert.Equal(shouldRecover ? original : legacyCurrent.RetryContextFingerprint, restored.LastDispatch!.RetryContextFingerprint);
        Assert.Equal(shouldRecover ? PaidRouteClassification.Paid : legacyCurrent.PaidRoute, restored.LastDispatch.PaidRoute);
        Assert.Equal(legacyCurrent.Command, restored.LastDispatch.Command);
        Assert.Equal(restored.LastDispatch, Assert.Single(restored.DispatchHistory));
    }

    [Xunit.Fact]
    public void RepeatingTheAdmissionMessageDoesNotAppendFeedback()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Repair the source", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var task = goal.Tasks.Single();
        kernel.RetryTaskAutomatically(goal.Id, task.Id, "Repair the source finding", RetryCause.NewSourceFinding);
        var admittedAt = task.LatestRetryAt;
        var events = goal.Timeline.Count;
        clock.Advance();

        kernel.RetryTaskAutomatically(goal.Id, task.Id, "Repair the source finding", RetryCause.NewSourceFinding);

        Assert.Equal(admittedAt, task.LatestRetryAt);
        Assert.Equal(events, goal.Timeline.Count);
        Assert.Empty(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetryFeedbackUpdated));
    }

    [Xunit.Fact]
    public void AutomaticFeedbackPreservesAuthoritativeAdmission()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Repair the source", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, "Keep the operator's required behavior", RetryCause.NewSourceFinding);
        var accepted = task.AcceptedRetryFeedback;
        var admittedAt = task.LatestRetryAt;

        kernel.RetryTaskAutomatically(goal.Id, task.Id, "Also repair the current source finding", RetryCause.NewSourceFinding);

        Assert.Equal(accepted, task.AcceptedRetryFeedback);
        Assert.Equal(admittedAt, task.LatestRetryAt);
        Assert.Single(goal.Timeline.Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;
        Assert.Contains(accepted!.Message, brief);
        Assert.Contains("Also repair the current source finding", brief);
    }

    [Xunit.Fact]
    public void ExecutedRetryStartsANewRoundAndRunningRetryIsRejected()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Repair the source", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RetryTaskAutomatically(goal.Id, task.Id, "Repair the source finding", RetryCause.NewSourceFinding);
        var admittedAt = task.LatestRetryAt;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "command", "worktree", clock.UtcNow));
        Assert.NotNull(task.LastDispatch);
        Assert.Throws<InvalidOperationException>(() => kernel.RetryTaskAutomatically(
            goal.Id, task.Id, "Repair the source finding", RetryCause.NewSourceFinding));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "command", "worktree", 1, "", "Source finding remains", clock.UtcNow));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        clock.Advance();

        kernel.RetryTaskAutomatically(goal.Id, task.Id, "Repair the source finding", RetryCause.NewSourceFinding);

        Assert.NotEqual(admittedAt, task.LatestRetryAt);
        Assert.Equal(2, goal.Timeline.Count(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
        Assert.Null(task.LastDispatch);
        Assert.Null(task.LastVerification);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void EquivalentPendingRetryAccruesFeedbackOnceAcrossRestart(bool assigned)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Repair all findings", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        if (assigned) kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RetryTask(goal.Id, task.Id, "Initial source finding", RetryCause.NewSourceFinding);
        var admittedAt = task.LatestRetryAt;
        var initialContext = RetryContextFingerprintFactory.Build(goal, task, "fixture", "model", PaidRouteClassification.Paid, null, null, null);
        clock.Advance();
        kernel.RetryTaskAutomatically(goal.Id, task.Id, "New source finding from review", RetryCause.NewSourceFinding);
        var updatedContext = RetryContextFingerprintFactory.Build(goal, task, "fixture", "model", PaidRouteClassification.Paid, null, null, null);
        Assert.NotEqual(initialContext, updatedContext);
        var persisted = JsonSerializer.Serialize(kernel.ExportSnapshot());
        kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(persisted)!, clock);
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single();
        clock.Advance();
        kernel.RetryTaskAutomatically(goal.Id, task.Id, "New source finding from review", RetryCause.NewSourceFinding);

        Assert.Equal(admittedAt, task.LatestRetryAt);
        Assert.Equal(assigned ? WorkTaskStatus.Assigned : WorkTaskStatus.Pending, task.Status);
        Assert.Single(goal.Timeline.Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
        var note = Assert.Single(goal.Timeline.Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetryFeedbackUpdated));
        Assert.Equal("New source finding from review", note.Message);
        Assert.Equal(updatedContext, RetryContextFingerprintFactory.Build(goal, task, "fixture", "model", PaidRouteClassification.Paid, null, null, null));
        Assert.Contains("New source finding from review", kernel.BuildTaskBrief(goal.Id, task.Id).Content);
    }

    [Xunit.Theory]
    [Xunit.InlineData(RetryCause.NewTestFinding, null, false)]
    [Xunit.InlineData(RetryCause.NewSourceFinding, RetryRoundKind.Mechanical, false)]
    [Xunit.InlineData(RetryCause.NewSourceFinding, null, true)]
    [Xunit.InlineData(RetryCause.Unknown, null, false)]
    public void DifferentOrAuthoritativeRetryStillReplacesPendingAdmission(
        RetryCause cause, RetryRoundKind? kind, bool authoritative)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Repair the current requirement", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        kernel.RetryTask(goal.Id, task.Id, "Initial finding",
            cause == RetryCause.Unknown ? RetryCause.Unknown : RetryCause.NewSourceFinding);
        var admittedAt = task.LatestRetryAt;
        clock.Advance();
        if (authoritative)
            kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, task.Id, "Current operator correction", cause, retryRoundKind: kind);
        else
            kernel.RetryTaskAutomatically(goal.Id, task.Id, "Different retry proposal", cause, retryRoundKind: kind);

        Assert.NotEqual(admittedAt, task.LatestRetryAt);
        Assert.Equal(2, goal.Timeline.Count(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal(cause, task.PendingRetryCause);
        Assert.Equal(kind, task.PendingRetryRoundKind);
        if (authoritative)
            Assert.Equal("Current operator correction", task.AcceptedRetryFeedback?.Message);
    }
}
