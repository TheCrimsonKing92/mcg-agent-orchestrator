using Mcg.AgentOrchestrator.Core;

public sealed class HumanInputSupersedeTests
{
    [Xunit.Fact]
    public void Supersede_AppendsHistory_RetractsPriorAnswers_AndRestoresBlockedTasks()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var planner = new TaskSpec(TaskId.New(), "Choose the recheck floor.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement recheck floor = 1 second.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Use recheck floor = 1 second.", [planner, developer]);
        var original = kernel.RequestHumanInput(goal.Id, planner.Id, "Which recheck floor should govern?");
        kernel.SubmitHumanInput(original.Id, "recheck floor = 1 second");
        var repeated = kernel.RequestHumanInput(
            goal.Id,
            developer.Id,
            "  WHICH recheck   floor should govern?  ");

        clock.Advance();
        var second = kernel.SupersedeHumanInput(
            goal.Id,
            original.Id,
            "recheck floor = 5 seconds",
            HumanInputAnswerOrigin.Operator);
        clock.Advance();
        var third = kernel.SupersedeHumanInput(
            goal.Id,
            original.Id,
            "recheck floor = 10 seconds",
            HumanInputAnswerOrigin.Operator);

        Assert.True(repeated.IsCompleted);
        Assert.Equal(original.Id, repeated.SupersededByRequestId);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Null(developer.LastDispatch);
        Assert.Null(developer.LastProcess);
        Assert.Equal("recheck floor = 10 seconds", original.Answer);
        Assert.Equal(3, original.AnswerHistory.Count);
        Assert.Equal(second.Id, original.AnswerHistory[0].SupersededByAnswerId);
        Assert.Equal(third.Id, original.AnswerHistory[1].SupersededByAnswerId);
        Assert.Null(original.AnswerHistory[2].SupersededByAnswerId);
        Assert.Equal([false, false, true], original.AnswerHistory.Select(answer => answer.Id == original.AuthoritativeAnswer?.Id));
        Assert.Equal(
            original.AnswerHistory.OrderBy(answer => answer.AnsweredAt).Select(answer => answer.Id),
            original.AnswerHistory.Select(answer => answer.Id));
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.HumanInputSuperseded);
    }

    [Xunit.Fact]
    public void BuildTaskBrief_UsesAuthoritativeAnswer_AndRedactsStaleCopies()
    {
        const string derivedBlocker = "exact-blocker - conflicting 1s/5s floor requires clarification";
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var planner = new TaskSpec(TaskId.New(), "Record `recheck floor = 1 second` in the plan.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the corrected floor.", AgentRole.Developer);
        var goal = kernel.CreateGoal(
            $"The earlier operator said recheck floor = 1 second. Prior finding: {derivedBlocker}",
            [planner, developer]);
        var request = kernel.RequestHumanInput(
            goal.Id,
            null,
            $"Which floor?{Environment.NewLine}Accompanying WORKER_RESULT blocker evidence: {derivedBlocker}");
        kernel.SubmitHumanInput(request.Id, "recheck floor = 1 second");
        kernel.SupersedeHumanInput(
            goal.Id,
            request.Id,
            "recheck floor = 5 seconds",
            HumanInputAnswerOrigin.Operator);

        var brief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;

        Assert.Contains("recheck floor = 5 seconds", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("recheck floor = 1 second", brief, StringComparison.Ordinal);
        Assert.DoesNotContain(derivedBlocker, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void BuildTaskBrief_RedactsContainingAnswer_WithoutCorruptingShortSubstrings()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var task = new TaskSpec(TaskId.New(), "Use 5 seconds; Notice remains visible.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Prior operator answers: Use 5 seconds and No. Notice remains visible.", [task]);
        var containing = kernel.RequestHumanInput(goal.Id, null, "Which duration?");
        kernel.SubmitHumanInput(containing.Id, "Use 5 seconds");
        kernel.SupersedeHumanInput(
            goal.Id,
            containing.Id,
            "5 seconds",
            HumanInputAnswerOrigin.Operator);
        var shortAnswer = kernel.RequestHumanInput(goal.Id, null, "Should the notice be removed?");
        kernel.SubmitHumanInput(shortAnswer.Id, "No");
        kernel.SupersedeHumanInput(
            goal.Id,
            shortAnswer.Id,
            "Keep it",
            HumanInputAnswerOrigin.Operator);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("5 seconds", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Use 5 seconds", brief, StringComparison.Ordinal);
        Assert.Contains("Notice remains visible", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Prior answer: No", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Supersede_RoundTripsHistory_AndLegacySingleAnswerLoadsAuthoritatively()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Wait for a choice.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Persist clarification history.", [task]);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which value?");
        kernel.SubmitHumanInput(request.Id, "A");
        kernel.SupersedeHumanInput(goal.Id, request.Id, "B", HumanInputAnswerOrigin.Operator);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredRequest = restored.GetHumanInputRequest(request.Id);
        Assert.Equal(["A", "B"], restoredRequest.AnswerHistory.Select(answer => answer.Text));
        Assert.True(restoredRequest.AnswerHistory[0].IsRetracted);
        Assert.Equal("B", restoredRequest.AuthoritativeAnswer?.Text);

        var legacySnapshot = kernel.ExportSnapshot();
        legacySnapshot = legacySnapshot with
        {
            HumanInputRequests = legacySnapshot.HumanInputRequests
                .Select(item => item with
                {
                    Answer = "legacy answer",
                    AnswerHistory = null
                })
                .ToArray()
        };
        var legacy = AgentOrchestratorKernel.FromSnapshot(legacySnapshot, clock)
            .GetHumanInputRequest(request.Id);
        var legacyAnswer = Assert.Single(legacy.AnswerHistory);
        Assert.Equal("legacy answer", legacyAnswer.Text);
        Assert.False(legacyAnswer.IsRetracted);
        Assert.Equal(legacyAnswer, legacy.AuthoritativeAnswer);
    }

    [Xunit.Fact]
    public void Supersede_ResolvesOnlyExactDerivedFinding_AndAllowsLaterReopen()
    {
        const string derivedBlocker = "exact-blocker - conflicting 1s/5s floor requires clarification";
        const string unrelated = "The 1 second timeout remains an unrelated implementation defect.";
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Review correction propagation.", [reviewer]);
        var request = kernel.RequestHumanInput(
            goal.Id,
            null,
            $"Which floor?{Environment.NewLine}Accompanying WORKER_RESULT blocker evidence: {derivedBlocker}");
        kernel.SubmitHumanInput(request.Id, "1 second");
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, ReviewVerification(
            clock.UtcNow,
            $$"""[{"stable_id":"derived","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"{{derivedBlocker}}"},{"stable_id":"unrelated","state":"open","location":{"file":"src/B.cs","region":"B.Run"},"description":"{{unrelated}}"}]"""));

        clock.Advance();
        kernel.SupersedeHumanInput(goal.Id, request.Id, "5 seconds", HumanInputAnswerOrigin.Operator);

        var afterSupersede = kernel.GetReviewFindingState(goal.Id);
        Assert.DoesNotContain(afterSupersede, finding => finding.StableId == "derived");
        Assert.Contains(afterSupersede, finding => finding.StableId == "unrelated");

        clock.Advance();
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, ReviewVerification(
            clock.UtcNow,
            $$"""[{"stable_id":"derived","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"{{derivedBlocker}}"},{"stable_id":"unrelated","state":"open","location":{"file":"src/B.cs","region":"B.Run"},"description":"{{unrelated}}"}]"""));

        Assert.Contains(kernel.GetReviewFindingState(goal.Id), finding => finding.StableId == "derived");
    }

    [Xunit.Fact]
    public void Supersede_RejectsWorkerOrigin_AndOrdinaryAnswerNamesCorrectionPath()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var task = new TaskSpec(TaskId.New(), "Wait for a choice.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Protect operator authority.", [task]);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which value?");
        kernel.SubmitHumanInput(request.Id, "A");

        var unauthorized = Assert.Throws<UnauthorizedAccessException>(() => kernel.SupersedeHumanInput(
            goal.Id,
            request.Id,
            "B",
            HumanInputAnswerOrigin.Worker));
        var alreadyAnswered = Assert.Throws<InvalidOperationException>(() =>
            kernel.SubmitHumanInput(request.Id, "B"));

        Assert.Contains("Only operator", unauthorized.Message, StringComparison.Ordinal);
        Assert.Contains("already been answered — use supersede", alreadyAnswered.Message, StringComparison.Ordinal);
        Assert.Equal("A", request.Answer);
        Assert.Single(request.AnswerHistory);
        Assert.Throws<KeyNotFoundException>(() => kernel.SupersedeHumanInput(
            goal.Id,
            HumanInputRequestId.New(),
            "B",
            HumanInputAnswerOrigin.Operator));
    }

    [Xunit.Fact]
    public void Supersede_resets_answered_duplicate_suppression_streak()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var task = new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reset duplicate streak.", [task]);
        const string fingerprint = "same-current-round-blocker";
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Which value?",
            blockerFingerprint: fingerprint).Request;
        kernel.SubmitHumanInput(request.Id, "A");
        kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Which value?", blockerFingerprint: fingerprint);
        kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Which value?", blockerFingerprint: fingerprint);
        Assert.Equal(2, request.SuppressionCount);

        kernel.SupersedeHumanInput(goal.Id, request.Id, "B", HumanInputAnswerOrigin.Operator);

        Assert.Equal(0, request.SuppressionCount);
        Assert.Equal(2, request.SuppressionAnswerRevision);
    }

    [Xunit.Fact]
    public void Supersede_restores_task_failed_at_answered_duplicate_threshold()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var task = new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Restore duplicate-threshold failure.", [task]);
        const string fingerprint = "same-current-round-blocker";
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Which value?",
            blockerFingerprint: fingerprint).Request;
        kernel.SubmitHumanInput(request.Id, "A");
        kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Which value?", blockerFingerprint: fingerprint);
        kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Which value?", blockerFingerprint: fingerprint);
        kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Which value?", blockerFingerprint: fingerprint);
        Assert.Equal(3, request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);

        kernel.SupersedeHumanInput(goal.Id, request.Id, "B", HumanInputAnswerOrigin.Operator);

        Assert.Equal(0, request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact]
    public void DuplicateHumanInput_EmitsOneAdvisoryAcrossDistinctTasks_Only()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var planner = new TaskSpec(TaskId.New(), "Plan.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Detect shared contradictions.", [planner, developer]);

        var first = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, "Which value?");
        var sameTask = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, " which   value? ");
        Assert.True(sameTask.WasReused);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ContradictoryRecordDetected);

        kernel.RequestHumanInputDeduplicated(goal.Id, developer.Id, "WHICH VALUE?");
        kernel.RequestHumanInputDeduplicated(goal.Id, developer.Id, "Which value?");

        var signal = Assert.Single(goal.Timeline.Where(evt => evt.Kind == ProgressKind.ContradictoryRecordDetected));
        Assert.Contains("distinctTasks=2", signal.Message, StringComparison.Ordinal);
        Assert.Contains("totalOccurrences=3", signal.Message, StringComparison.Ordinal);
        Assert.Contains(first.Request.Id.Value, signal.Message, StringComparison.Ordinal);
        Assert.Equal(2, AgentOrchestratorKernel.DuplicateHumanInputDistinctTaskThreshold);
    }

    private static TaskVerificationRecord ReviewVerification(DateTimeOffset completedAt, string findings) =>
        new(
            "review",
            "C:\\repo",
            1,
            $"""
            WORKER_RESULT:
            findings: {findings}
            touched_anchors: []
            verdict: needs-work
            END_WORKER_RESULT
            """,
            string.Empty,
            completedAt);
}
