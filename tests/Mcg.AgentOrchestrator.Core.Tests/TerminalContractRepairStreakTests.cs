using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: hand-built history, timeline and injected clock only.
public sealed class TerminalContractRepairStreakTests
{
    [Theory]
    [InlineData(ProgressKind.TaskRetried, "Invalidated Tester task because upstream Developer task abcd1234 was retried.")]
    [InlineData(ProgressKind.TaskRetried, "Invalidated Tester task because retried upstream Developer task abcd1234 changed candidate from a to b.")]
    [InlineData(ProgressKind.TaskRetried, "Invalidated superseded Tester verdict for reviewed candidate a because a later verification superseded that stored verdict; historical findings remain in verification history.")]
    [InlineData(ProgressKind.TaskNote, "TERMINAL_CONTRACT_REPAIR_STREAK_RESET task=fixture reason=operator-retry source=operator")]
    public void CandidateResetStartsFreshStreak(ProgressKind kind, string message)
    {
        var (task, _, between) = ThreeRecords();
        Assert.Equal(3, TerminalContractRepairStreak.Count([], task));
        var reset = Event(task.Id, kind, message, between);
        Assert.Equal(between, TerminalContractRepairStreak.LatestResetAt([reset], task.Id));
        Assert.Equal(1, TerminalContractRepairStreak.Count([reset], task));
    }

    [Theory]
    [InlineData(ProgressKind.TaskRetried, "Tester terminal contract repair 1/2; same-role retry: ...", false)]
    [InlineData(ProgressKind.TaskNote, "TERMINAL_CONTRACT_REPAIR_STREAK_RESET task=fixture", true)]
    [InlineData(ProgressKind.TaskNote, "Invalidated Tester task because upstream Developer was retried.", false)]
    [InlineData(ProgressKind.TaskRetried, "invalidated Tester task because upstream Developer was retried.", false)]
    [InlineData(ProgressKind.TaskRetried, "TERMINAL_CONTRACT_REPAIR_STREAK_RESET task=fixture", false)]
    public void UnrelatedEventsPreserveBaseline(ProgressKind kind, string message, bool otherTask)
    {
        var (task, _, between) = ThreeRecords();
        var baseline = TerminalContractRepairStreak.Count([], task);
        Assert.Equal(3, baseline);
        var evt = Event(otherTask ? TaskId.New() : task.Id, kind, message, between);
        Assert.Null(TerminalContractRepairStreak.LatestResetAt([evt], task.Id));
        Assert.Equal(baseline, TerminalContractRepairStreak.Count([evt], task));
    }

    [Fact]
    public void ResetTieExcludesEarlierRecordAndGreatestTimestampWins()
    {
        var (task, _, _) = ThreeRecords();
        var first = task.VerificationHistory[0].CompletedAt;
        var second = task.VerificationHistory[1].CompletedAt;
        var latest = Event(task.Id, ProgressKind.TaskRetried, "Invalidated latest", second);
        var older = Event(task.Id, ProgressKind.TaskNote, "TERMINAL_CONTRACT_REPAIR_STREAK_RESET older", first);
        Assert.Equal(second, TerminalContractRepairStreak.LatestResetAt([latest, older], task.Id));
        Assert.Equal(1, TerminalContractRepairStreak.Count([latest, older], task));
    }

    [Fact]
    public void FutureResetStillCountsLatestOnlyWhenItIsAStreakRecord()
    {
        var (task, clock, _) = ThreeRecords();
        clock.Advance();
        var reset = Event(task.Id, ProgressKind.TaskNote, "TERMINAL_CONTRACT_REPAIR_STREAK_RESET future", clock.UtcNow);
        Assert.Equal(1, TerminalContractRepairStreak.Count([reset], task));
        task.RecordVerification(Record(clock, "valid"));
        clock.Advance();
        reset = reset with { OccurredAt = clock.UtcNow };
        Assert.Equal(0, TerminalContractRepairStreak.Count([reset], task));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void WithoutResetEveryHistoryMatchesOriginalTrailingPredicate(int mask)
    {
        var clock = new FakeClock();
        var task = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        Assert.Equal(0, TerminalContractRepairStreak.Count([], task));
        for (var index = 0; index < 3; index++)
        {
            clock.Advance();
            task.RecordVerification(Record(clock, (mask & (1 << index)) != 0 ? "incomplete" : "valid"));
        }
        var expected = task.VerificationHistory.Reverse().TakeWhile(record =>
            record.CompletionVerdictRule == TaskOutcomeRules.VerificationTerminalContractIncomplete.Token ||
            DispatchFailureClassifier.IsProviderInterruptionFailure(record) ||
            (WorkerResultBlockers.TryGetTestsStatus(record, out var status) &&
             status == WorkerResultBlockers.TestsStatus.Inconclusive)).Count();
        Assert.Equal(expected, TerminalContractRepairStreak.Count([], task));
    }

    [Theory]
    [InlineData("incomplete", CriteriaCorrectionSource.Operator, "operator")]
    [InlineData("interrupted", CriteriaCorrectionSource.Operator, "operator")]
    [InlineData("inconclusive", CriteriaCorrectionSource.Operator, "operator")]
    [InlineData("incomplete", CriteriaCorrectionSource.AgentIntent, "agent-intent")]
    [InlineData("interrupted", CriteriaCorrectionSource.AgentIntent, "agent-intent")]
    [InlineData("inconclusive", CriteriaCorrectionSource.AgentIntent, "agent-intent")]
    public void EligibleOperatorRetryHasExactNote(string category, CriteriaCorrectionSource source, string token)
    {
        var task = new TaskSpec(new TaskId("0123456789abcdef0123456789abcdef"), "Verify", AgentRole.Tester);
        task.RecordVerification(Record(new FakeClock(), category));
        Assert.True(TerminalContractRepairStreak.IsStreakRecord(task.VerificationHistory[0]));
        Assert.Equal($"TERMINAL_CONTRACT_REPAIR_STREAK_RESET task={task.Id.Value} reason=operator-retry source={token}",
            TerminalContractRepairStreak.OperatorRetryResetNote(task, source));
    }

    [Theory]
    [InlineData(AgentRole.Tester, CriteriaCorrectionSource.Escalation, "incomplete")]
    [InlineData(AgentRole.Tester, CriteriaCorrectionSource.WorkerResult, "incomplete")]
    [InlineData(AgentRole.Reviewer, CriteriaCorrectionSource.Operator, "incomplete")]
    [InlineData(AgentRole.Developer, CriteriaCorrectionSource.Operator, "incomplete")]
    [InlineData(AgentRole.Tester, CriteriaCorrectionSource.Operator, "empty")]
    [InlineData(AgentRole.Tester, CriteriaCorrectionSource.Operator, "valid")]
    public void IneligibleRetryHasNoNote(AgentRole role, CriteriaCorrectionSource source, string category)
    {
        var task = new TaskSpec(TaskId.New(), "Verify", role);
        if (category != "empty")
            task.RecordVerification(Record(new FakeClock(), category));
        Assert.Null(TerminalContractRepairStreak.OperatorRetryResetNote(task, source));
    }

    private static (TaskSpec Task, FakeClock Clock, DateTimeOffset Between) ThreeRecords()
    {
        var clock = new FakeClock();
        var task = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        task.RecordVerification(Record(clock, "incomplete"));
        clock.Advance();
        task.RecordVerification(Record(clock, "incomplete"));
        clock.Advance();
        var between = clock.UtcNow;
        clock.Advance();
        task.RecordVerification(Record(clock, "incomplete"));
        return (task, clock, between);
    }

    private static ProgressEvent Event(TaskId taskId, ProgressKind kind, string message, DateTimeOffset at) =>
        new(GoalId.New(), taskId, kind, message, at);

    private static TaskVerificationRecord Record(FakeClock clock, string category)
    {
        var output = "WORKER_RESULT:\nfiles: none\ncommands: inspected receipt\ntests: " +
            (category == "inconclusive" ? "inconclusive - receipt unavailable" : "pass - gate-owned.trx; Passed: 1") +
            "\ncommit: none\nblockers: none\nmodel_fit: fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
        return new TaskVerificationRecord("fixture", @"C:\repo", 0, output,
            category == "interrupted" ? "ERROR: permission_cancelled" : "", clock.UtcNow,
            WorkerResultPresent: true, EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: clock.UtcNow,
            CompletionVerdictVerifiedSuccess: category == "valid",
            CompletionVerdictRule: category == "incomplete" ? TaskOutcomeRules.VerificationTerminalContractIncomplete.Token : null);
    }
}
