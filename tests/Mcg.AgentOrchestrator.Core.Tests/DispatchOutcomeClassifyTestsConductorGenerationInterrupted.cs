using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: in-memory goals and a private MutableClock, no process or shared state.
public sealed class DispatchOutcomeClassifyTestsConductorGenerationInterrupted
{
    private const string Receipt = "CONDUCTOR_GENERATION_INTERRUPTED owner_pid=4242 owner_started_at=2026-10-19T08:18:07.0000000+00:00 terminated_at=2026-10-19T08:37:23.0000000+00:00 cause=activation-reverted heartbeat_at=2026-10-19T08:37:22.5000000+00:00 dispatch_started_at=2026-10-19T08:34:20.0000000+00:00";
    private static readonly string Marker = DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ConductorGenerationInterrupted);

    [Fact]
    public void Classify_MarkedFailure_PrefersEnvironmentalRetryRule()
    {
        var clock = new MutableClock();
        var task = new TaskSpec(TaskId.New(), "Recover", AgentRole.Developer);
        var verification = Verification(clock, "  " + Marker + "  \r\n" + Receipt);
        var outcome = DispatchFailureClassifier.Classify(task, verification);
        Assert.Equal("conductor-generation-interrupted", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal(TaskOutcomeClass.Environmental, TaskOutcomeRules.Known["conductor-generation-interrupted"].Class);
        Assert.Equal("conductor-generation-interrupted: " + Receipt, outcome.EvidenceSummary);
        Assert.Equal(1, outcome.ExitCode);
    }

    [Theory]
    [InlineData("prefix ")]
    [InlineData("")]
    public void Classify_NoExactAuthoritativeMarker_PreservesUnknownFailure(string prefix)
    {
        var clock = new MutableClock();
        var task = new TaskSpec(TaskId.New(), "Recover", AgentRole.Developer);
        var baseline = Verification(clock, Receipt);
        var control = prefix.Length == 0
            ? baseline with { StandardError = Marker + "\n" + Receipt, AuthoritativeStandardError = Receipt }
            : Verification(clock, prefix + Marker + "\n" + Receipt);
        var ordinary = DispatchFailureClassifier.Classify(task, baseline);
        var outcome = DispatchFailureClassifier.Classify(task, control);
        Assert.Equal("unknown-failure", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(ordinary.Kind, outcome.Kind);
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, ordinary.Kind);
        Assert.Equal("unknown-failure", TaskOutcomeClassifier.TryExtractRule(ordinary.ClassifierReceipt));
        Assert.Equal("unknown-failure stderr-tail: " + Receipt[..200], ordinary.EvidenceSummary);
        if (prefix.Length == 0) Assert.Equal(ordinary.EvidenceSummary, outcome.EvidenceSummary);
    }

    [Fact]
    public void Outcome_MissingReceipt_UsesExplicitFallback()
    {
        var clock = new MutableClock();
        var outcome = ConductorGenerationInterruptionClassification.Outcome(Verification(clock, Marker));
        Assert.Equal("conductor-generation-interrupted: receipt-missing", outcome.EvidenceSummary);
    }

    [Fact]
    public void RecordDispatch_FourMarkedRounds_UsesExistingBoundedRetryBudget()
    {
        var clock = new MutableClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Bound interrupted generation retries");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(item => item.RequiredRole == AgentRole.Developer);
        var operatorAdmissions = task.RetryAdmissionHistory.Count;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            kernel.RecordTaskDispatch(goal.Id, task.Id, new("codex-cli", "codex exec", "C:\\repo", clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, Verification(clock, Marker + "\n" + Receipt));
            Assert.Equal(attempt, DispatchFailureClassifier.CountConsecutiveProviderInterruptionFailures(task));
            Assert.Equal(attempt, goal.Timeline.Count(evt => evt.TaskId == task.Id &&
                evt.Kind == ProgressKind.TaskNote &&
                TaskOutcomeClassifier.TryExtractRule(evt.Message) == "conductor-generation-interrupted"));
            if (attempt <= 3)
            {
                Assert.Contains(task.Status, new[] { WorkTaskStatus.Pending, WorkTaskStatus.Assigned });
                Assert.Equal(clock.UtcNow.AddMinutes(attempt), task.SubscriptionRetryAfter);
                Assert.Equal(RetryCause.ProviderInterruption, task.PendingRetryCause);
                Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried && evt.Message.Contains($"attempt {attempt}/3", StringComparison.Ordinal));
            }
            Assert.Equal(operatorAdmissions, task.RetryAdmissionHistory.Count);
            Assert.NotEqual(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Null(goal.RetainedAcceptanceFailure);
            clock.Advance(TimeSpan.FromMinutes(5));
        }
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(3, goal.Timeline.Count(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
        Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed && evt.Message.Contains("automatic retry budget 3 exhausted", StringComparison.Ordinal));
    }

    private static TaskVerificationRecord Verification(IClock clock, string error) =>
        new("codex exec", "C:\\repo", 1, string.Empty, error, clock.UtcNow);
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-10-19T08:38:25Z");
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}
