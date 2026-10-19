using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: subscription dispatch records only; no providers, files or processes are invoked.
public sealed class TerminalContractRepairStreakKernelTests
{
    [Theory]
    [InlineData(CriteriaCorrectionSource.Operator, "operator")]
    [InlineData(CriteriaCorrectionSource.AgentIntent, "agent-intent")]
    public void DeliberateRetryGrantsAnotherFullRepairBudget(CriteriaCorrectionSource source, string token)
    {
        var fixture = new Fixture();
        fixture.Exhaust();
        fixture.Clock.Advance();
        if (source == CriteriaCorrectionSource.Operator)
            fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Task.Id, "Operator retry.", RetryCause.NewTestFinding);
        else
            fixture.Kernel.RetryTaskWithAuthoritativeFeedback(fixture.Goal.Id, fixture.Task.Id,
                "Agent retry.", RetryCause.NewTestFinding, correctionSource: source);

        var note = Assert.Single(ResetNotes(fixture.Goal));
        Assert.Equal(fixture.Task.Id, note.TaskId);
        Assert.Equal($"TERMINAL_CONTRACT_REPAIR_STREAK_RESET task={fixture.Task.Id.Value} reason=operator-retry source={token}", note.Message);
        fixture.Round();
        fixture.AssertRepair(1);
        fixture.Round();
        fixture.AssertRepair(2);
        fixture.Round();
        fixture.AssertExhausted();
        Assert.Single(ResetNotes(fixture.Goal));
    }

    [Fact]
    public void DeveloperInvalidationAloneStartsFreshTesterBudget()
    {
        var fixture = new Fixture(withDeveloper: true);
        fixture.Exhaust();
        fixture.Clock.Advance();
        var developer = fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        fixture.Kernel.RetryTask(fixture.Goal.Id, developer.Id, "Rework candidate.", RetryCause.NewSourceFinding);

        Assert.Contains(fixture.Goal.Timeline, evt => evt.TaskId == fixture.Task.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message == $"Invalidated Tester task because upstream Developer task {developer.Id.Value[..8]} was retried.");
        Assert.Empty(ResetNotes(fixture.Goal));
        fixture.Round();
        fixture.AssertRepair(1);
    }

    [Fact]
    public void InPlaceMalformedRoundsKeepExactOriginalMessagesAndBudget()
    {
        var fixture = new Fixture();
        var expected = new List<string>();
        for (var round = 1; round <= 3; round++)
        {
            var summary = fixture.Round();
            expected.Add(round <= 2
                ? $"Tester terminal contract repair {round}/2; same-role retry: {summary}"
                : $"Tester terminal contract repair budget exhausted; operator action required: {summary}");
            if (round <= 2)
                fixture.AssertRepair(round);
            else
                fixture.AssertExhausted();
        }

        Assert.Equal(expected, fixture.Goal.Timeline.Where(evt => evt.TaskId == fixture.Task.Id &&
            evt.Kind is ProgressKind.TaskRetried or ProgressKind.TaskFailed).Select(evt => evt.Message));
        Assert.Empty(ResetNotes(fixture.Goal));
    }

    [Fact]
    public void ProviderInterruptionBetweenMalformedRoundsStillConsumesStreak()
    {
        var fixture = new Fixture();
        fixture.Round();
        fixture.AssertRepair(1);
        fixture.Round(interrupted: true);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.True(DispatchFailureClassifier.IsProviderInterruptionFailure(fixture.Task.VerificationHistory[^1]));
        fixture.Round();
        fixture.AssertExhausted();
        Assert.Equal(3, TerminalContractRepairStreak.Count(fixture.Goal.Timeline, fixture.Task));
        Assert.Empty(ResetNotes(fixture.Goal));
    }

    [Fact]
    public void AutomaticRetryDoesNotResetExhaustedBudget()
    {
        var fixture = new Fixture();
        fixture.Exhaust();
        fixture.Clock.Advance();
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Task.Id, "Automatic retry.", RetryCause.ProviderInterruption);
        Assert.Empty(ResetNotes(fixture.Goal));
        fixture.Round();
        fixture.AssertExhausted();
        Assert.Equal(4, TerminalContractRepairStreak.Count(fixture.Goal.Timeline, fixture.Task));
    }

    [Fact]
    public void ValidTesterOperatorRetryDoesNotAddResetNote()
    {
        var fixture = new Fixture();
        fixture.Round(valid: true);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.True(fixture.Task.LastVerification!.CompletionVerdictVerifiedSuccess);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(fixture.Task, fixture.Task.LastVerification).Status);
        fixture.Clock.Advance();
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Task.Id, "Operator retry.", RetryCause.NewTestFinding);
        Assert.Empty(ResetNotes(fixture.Goal));
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Reviewer)]
    public void OtherRoleOperatorRetryDoesNotAddResetNoteEvenAfterIncompleteVerdict(AgentRole role)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Rework", role);
        var goal = kernel.CreateGoal("Retry other role", [task]);
        kernel.ActivateGoal(goal.Id, [Agent(role)]);
        task.RecordVerification(new TaskVerificationRecord("fixture", @"C:\repo", 0, "Incomplete", "", clock.UtcNow,
            CompletionVerdictRule: TaskOutcomeRules.VerificationTerminalContractIncomplete.Token));
        Assert.True(TerminalContractRepairStreak.IsStreakRecord(task.VerificationHistory[^1]));
        clock.Advance();
        kernel.RetryTask(goal.Id, task.Id, "Operator retry.", RetryCause.NewTestFinding);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Empty(ResetNotes(goal));
    }

    [Fact]
    public void SnapshotRestoresResetBoundaryAndNextRepair()
    {
        var fixture = new Fixture();
        fixture.Exhaust();
        fixture.Clock.Advance();
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Task.Id, "Operator retry.", RetryCause.NewTestFinding);
        var originalCount = TerminalContractRepairStreak.Count(fixture.Goal.Timeline, fixture.Task);
        Assert.Equal(1, originalCount);
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), fixture.Clock);
        var goal = restored.GetGoal(fixture.Goal.Id);
        var task = restored.GetTask(goal.Id, fixture.Task.Id);
        Assert.Equal(originalCount, TerminalContractRepairStreak.Count(goal.Timeline, task));
        Assert.Equal(Assert.Single(ResetNotes(fixture.Goal)).Message, Assert.Single(ResetNotes(goal)).Message);
        var restoredFixture = new Fixture(restored, goal, task, fixture.Clock);
        restoredFixture.Round();
        restoredFixture.AssertRepair(1);
    }

    private static IEnumerable<ProgressEvent> ResetNotes(Goal goal) => goal.Timeline.Where(evt =>
        evt.Kind == ProgressKind.TaskNote && evt.Message.StartsWith("TERMINAL_CONTRACT_REPAIR_STREAK_RESET ", StringComparison.Ordinal));

    private static AgentDefinition Agent(AgentRole role) => new(AgentId.New(), role.ToString(), role,
        new ModelProfile("Test", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey));

    private static string Contract() =>
        "WORKER_RESULT:\nfiles: none\ncommands: inspected gate receipt\ntests: pass - gate-owned.trx; Passed: 1" +
        "\ncommit: none\nblockers: none\nmodel_fit: fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    private static TaskVerificationRecord Evidence(TaskSpec task, FakeClock clock, string output, bool interrupted) =>
        new(task.LastDispatch!.Command, task.LastDispatch.WorkingDirectory, 0, output,
            interrupted ? "ERROR: permission_cancelled" : "", clock.UtcNow,
            WorkerResultPresent: true, DispatchStartedAt: task.LastDispatch.DispatchedAt,
            EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: task.LastDispatch.DispatchedAt);

    private sealed class Fixture
    {
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Task { get; }
        public FakeClock Clock { get; }

        public Fixture(bool withDeveloper = false)
        {
            Clock = new FakeClock();
            Kernel = new AgentOrchestratorKernel(Clock);
            Task = new TaskSpec(TaskId.New(), "Verify candidate", AgentRole.Tester);
            Goal = Kernel.CreateGoal("Repair candidate terminal contract", withDeveloper
                ? [new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer), Task]
                : [Task]);
            Kernel.ActivateGoal(Goal.Id, withDeveloper ? [Agent(AgentRole.Developer), Agent(AgentRole.Tester)] : [Agent(AgentRole.Tester)]);
        }

        public Fixture(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, FakeClock clock) =>
            (Kernel, Goal, Task, Clock) = (kernel, goal, task, clock);

        public string Round(bool valid = false, bool interrupted = false)
        {
            Clock.Advance();
            Kernel.RecordTaskDispatch(Goal.Id, Task.Id,
                new TaskDispatchRecord("grok-cli", "grok review", @"C:\repo", Clock.UtcNow, WorkerProviderKind: ProviderKind.XaiGrokCli));
            Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Running, "Fixture attempt started.");
            Clock.Advance();
            var evidence = Evidence(Task, Clock, valid || interrupted ? Contract() : "Introduction only.", interrupted);
            var outcome = DispatchFailureClassifier.Classify(Task, evidence);
            Assert.Equal(interrupted ? DispatchOutcomeKind.ProviderInterruption :
                valid ? DispatchOutcomeKind.VerifiedSuccess : DispatchOutcomeKind.VerificationIncomplete, outcome.Kind);
            var previousCount = Task.VerificationHistory.Count;
            Kernel.RecordDispatchExecutionResult(Goal.Id, Task.Id, evidence);
            Assert.Equal(previousCount + 1, Task.VerificationHistory.Count);
            Assert.Equal(Clock.UtcNow, Task.VerificationHistory[^1].CompletedAt);
            return outcome.EvidenceSummary;
        }

        public void Exhaust()
        {
            Round();
            AssertRepair(1);
            Round();
            AssertRepair(2);
            Round();
            AssertExhausted();
        }

        public void AssertRepair(int repair)
        {
            Assert.Equal(WorkTaskStatus.Assigned, Task.Status);
            var evt = Goal.Timeline.Last(evt => evt.TaskId == Task.Id && evt.Kind == ProgressKind.TaskRetried);
            Assert.StartsWith($"Tester terminal contract repair {repair}/2; same-role retry:", evt.Message);
        }

        public void AssertExhausted()
        {
            Assert.Equal(WorkTaskStatus.Failed, Task.Status);
            var evt = Goal.Timeline.Last(evt => evt.TaskId == Task.Id && evt.Kind == ProgressKind.TaskFailed);
            Assert.StartsWith("Tester terminal contract repair budget exhausted; operator action required:", evt.Message);
        }
    }
}
