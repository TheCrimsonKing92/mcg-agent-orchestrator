using Mcg.AgentOrchestrator.Core;

public sealed class ReviewFindingParseRejectionJournalTests
{
    [Xunit.Theory]
    [Xunit.InlineData(
        "findings: [{\"stable_id\":\"missing-anchor\",\"state\":\"open\",\"severity\":\"blocking\",\"category\":\"correctness\",\"location\":{\"file\":\"src/Target.cs\",\"region\":\"Run\"},\"description\":\"broken\"}]",
        "touched_anchors_fragment=<missing>")]
    [Xunit.InlineData(
        "findings: [{\"stable_id\":\"missing-description\",\"state\":\"open\",\"severity\":\"blocking\",\"category\":\"correctness\",\"location\":{\"file\":\"src/Target.cs\",\"region\":\"Run\"}}]\ntouched_anchors: []",
        "missing-description")]
    public void RejectedFindingRoundRecordsDiagnosticAndFragment(string fields, string expectedFragment)
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review malformed findings", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Diagnose malformed findings", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(
            goal.Id,
            reviewer.Id,
            new TaskDispatchRecord("reviewer", "review", "C:\\tmp", DateTimeOffset.UtcNow));
        var output = string.Join(
            "\n",
            "WORKER_RESULT:",
            fields,
            "blockers: src/Target.cs:1 - blocking defect",
            "verdict: needs-work",
            "END_WORKER_RESULT");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            output,
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        var note = Xunit.Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Rejected Reviewer structured finding result", StringComparison.Ordinal)));
        Xunit.Assert.Contains("diagnostic=", note.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(expectedFragment, note.Message, StringComparison.Ordinal);
    }
}
