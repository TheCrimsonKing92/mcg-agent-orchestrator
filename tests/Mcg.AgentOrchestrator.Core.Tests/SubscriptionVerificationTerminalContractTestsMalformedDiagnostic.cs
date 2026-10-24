using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe recorded-evidence fixtures; no provider, filesystem or process access.
public sealed class SubscriptionVerificationTerminalContractTestsMalformedDiagnostic
{
    private const string InvalidCriteria =
        "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"maybe\",\"evidence\":\"x\"}]";
    private const string CriteriaDiagnostic =
        "Reviewer field criteria_verdicts is malformed: criteria_verdicts JSON contains an invalid criterion_index, verdict, or evidence value.";
    private const string TestsDiagnostic =
        "Terminal field tests must start with pass, fail, deferred, not-run or 'inconclusive - <evidence>'; got 'maybe - x'.";
    private const string BlockersDiagnostic =
        "Terminal field blockers must start with none or a blocker description; got '(none)'.";
    private const string VerdictDiagnostic =
        "Reviewer field verdict must be pass, needs-work, fail or blocked-at-cap; got 'unknown'.";
    private const string InconclusiveDiagnostic =
        "tests: inconclusive requires 'inconclusive - <current-round evidence>'.";

    [Theory]
    [InlineData("verdict: pass", "verdict: unknown", VerdictDiagnostic)]
    [InlineData("findings: []", "findings: invalid", null)]
    [InlineData("criteria_verdicts: []", InvalidCriteria, CriteriaDiagnostic)]
    public void ReviewerDiagnosticNamesTheFailingField(
        string original, string replacement, string? expectedDiagnostic)
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var record = Evidence(task, at, Contract(AgentRole.Reviewer).Replace(original, replacement));
        if (original == "findings: []")
        {
            Assert.False(WorkerResultBlockers.TryFindReviewFindingRound(record, out _, out var reason));
            Assert.NotEmpty(reason);
            expectedDiagnostic = "Reviewer field findings is malformed: " + reason;
        }

        var assessment = WorkerResultBlockers.AssessSubscriptionVerification(task, record);

        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
        Assert.Equal(expectedDiagnostic, assessment.Diagnostic);
    }

    [Theory]
    [InlineData("tests: pass - gate-owned.trx; Passed: 1", "tests: maybe - x", TestsDiagnostic)]
    [InlineData("tests: pass - gate-owned.trx; Passed: 1", "tests: inconclusive", InconclusiveDiagnostic)]
    [InlineData("blockers: none", "blockers: (none)", BlockersDiagnostic)]
    public void TesterDiagnosticNamesTheFailingField(
        string original, string replacement, string expectedDiagnostic)
    {
        var (_, _, task, at) = Dispatch(AgentRole.Tester);
        var record = Evidence(task, at, Contract(AgentRole.Tester).Replace(original, replacement));

        var assessment = WorkerResultBlockers.AssessSubscriptionVerification(task, record);

        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
        Assert.Equal(expectedDiagnostic, assessment.Diagnostic);
    }

    [Fact]
    public void LongFieldValueIsQuotedWithinBound()
    {
        var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(
            AgentRole.Tester, Contract(AgentRole.Tester, "  " + new string('y', 300) + "  "));

        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
        Assert.Equal(
            "Terminal field tests must start with pass, fail, deferred, not-run or 'inconclusive - <evidence>'; got '" +
            new string('y', 160) + "...'.", assessment.Diagnostic);
        Assert.DoesNotContain(new string('y', 161), assessment.Diagnostic);
    }

    [Theory]
    [InlineData("tests: inconclusive", "blockers: (none)", InconclusiveDiagnostic)]
    [InlineData("tests: maybe - x", "blockers: (none)", TestsDiagnostic)]
    [InlineData("tests: deferred - AcceptanceTests", "blockers: (none)", BlockersDiagnostic)]
    [InlineData("tests: deferred - AcceptanceTests", "blockers: none", VerdictDiagnostic)]
    public void FirstMalformedCheckWins(string testsLine, string blockersLine, string expectedDiagnostic)
    {
        var output = Contract(AgentRole.Reviewer)
            .Replace("tests: deferred - AcceptanceTests", testsLine)
            .Replace("blockers: none", blockersLine)
            .Replace("verdict: pass", "verdict: unknown")
            .Replace("findings: []", "findings: invalid")
            .Replace("criteria_verdicts: []", InvalidCriteria);

        var assessment = WorkerResultBlockers.AssessTerminalVerificationOutput(AgentRole.Reviewer, output);

        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
        Assert.Equal(expectedDiagnostic, assessment.Diagnostic);
    }

    [Fact]
    public void FindingsDiagnosticPrecedesCriteriaDiagnostic()
    {
        var (_, _, task, at) = Dispatch(AgentRole.Reviewer);
        var output = Contract(AgentRole.Reviewer)
            .Replace("findings: []", "findings: invalid")
            .Replace("criteria_verdicts: []", InvalidCriteria);
        var record = Evidence(task, at, output);
        Assert.False(WorkerResultBlockers.TryFindReviewFindingRound(record, out _, out var reason));
        Assert.NotEmpty(reason);

        var assessment = WorkerResultBlockers.AssessSubscriptionVerification(task, record);

        Assert.Equal(VerificationTerminalContractStatus.Malformed, assessment.Status);
        Assert.Equal("Reviewer field findings is malformed: " + reason, assessment.Diagnostic);
    }

    [Fact]
    public void RepairRetryCarriesTheSpecificDiagnostic()
    {
        var (kernel, goal, task, at) = Dispatch(AgentRole.Reviewer);
        var output = Contract(AgentRole.Reviewer).Replace("criteria_verdicts: []", InvalidCriteria);
        var record = Evidence(task, at, output);
        var outcome = DispatchFailureClassifier.Classify(task, record);
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, outcome.Kind);
        Assert.Equal("terminal_contract=Malformed; " + CriteriaDiagnostic, outcome.EvidenceSummary);

        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, record);

        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        var retry = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal("Reviewer terminal contract repair 1/2; same-role retry: terminal_contract=Malformed; " +
            CriteriaDiagnostic, retry.Message);
        Assert.Contains(CriteriaDiagnostic, FeedbackBlock(kernel.BuildTaskBrief(goal.Id, task.Id).Content));
    }

    [Fact]
    public void UnchangedStatusesKeepTheirDiagnostics()
    {
        foreach (var role in new[] { AgentRole.Reviewer, AgentRole.Tester })
        {
            var missing = WorkerResultBlockers.AssessTerminalVerificationOutput(role, "Introduction only.");
            Assert.Equal(VerificationTerminalContractStatus.Missing, missing.Status);
            Assert.Equal("Current-attempt stdout has no terminal WORKER_RESULT.", missing.Diagnostic);

            var unfinished = WorkerResultBlockers.AssessTerminalVerificationOutput(
                role, Contract(role).Replace("END_WORKER_RESULT", ""));
            Assert.Equal(VerificationTerminalContractStatus.Malformed, unfinished.Status);
            Assert.Equal("Final WORKER_RESULT is missing END_WORKER_RESULT.", unfinished.Diagnostic);

            var malformed = WorkerResultBlockers.AssessTerminalVerificationOutput(
                role, Contract(role, "maybe - x"));
            Assert.Equal(VerificationTerminalContractStatus.Malformed, malformed.Status);
            Assert.Equal(TestsDiagnostic, malformed.Diagnostic);

            var valid = WorkerResultBlockers.AssessTerminalVerificationOutput(role, Contract(role));
            Assert.Equal(VerificationTerminalContractStatus.Valid, valid.Status);
            Assert.Empty(valid.Diagnostic);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, DateTimeOffset At) Dispatch(AgentRole role)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Require terminal evidence", [new TaskSpec(TaskId.New(), "Verify candidate", role)]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(AgentId.New(), role.ToString(), role,
            new ModelProfile("Test", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = Assert.Single(goal.Tasks);
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("grok-cli", "grok review", @"C:\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.XaiGrokCli));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Fixture attempt started.");
        return (kernel, goal, task, clock.UtcNow);
    }

    private static TaskVerificationRecord Evidence(TaskSpec task, DateTimeOffset at, string output) =>
        new(task.LastDispatch!.Command, task.LastDispatch.WorkingDirectory, 0, output, "", at.AddSeconds(1),
            WorkerResultPresent: true, DispatchStartedAt: at, EvidenceSource: VerificationEvidenceSource.Dispatch, DispatchAttemptAt: at);

    private static string Contract(AgentRole role, string? tests = null) =>
        "WORKER_RESULT:\nfiles: none\ncommands: inspected gate receipt\ntests: " +
        (tests ?? (role == AgentRole.Tester ? "pass - gate-owned.trx; Passed: 1" : "deferred - AcceptanceTests")) +
        "\ncommit: none\nblockers: none\nmodel_fit: fixture\nskills: none\nconfidence: high\n" +
        (role == AgentRole.Reviewer ? "findings: []\ntouched_anchors: []\ncriteria_verdicts: []\nverdict: pass\n" : "") +
        "END_WORKER_RESULT";

    private static string FeedbackBlock(string brief)
    {
        const string startMarker = "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->";
        const string endMarker = "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->";
        var start = brief.IndexOf(startMarker, StringComparison.Ordinal);
        var end = brief.IndexOf(endMarker, start >= 0 ? start : 0, StringComparison.Ordinal);
        Assert.True(start >= 0, "Accumulated retry feedback start marker was absent.");
        Assert.True(end > start, "Accumulated retry feedback end marker was absent.");
        return brief[start..(end + endMarker.Length)];
    }
}
