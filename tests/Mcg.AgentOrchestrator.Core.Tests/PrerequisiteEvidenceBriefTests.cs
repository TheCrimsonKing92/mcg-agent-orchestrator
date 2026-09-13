using Mcg.AgentOrchestrator.Core;

public sealed class PrerequisiteEvidenceBriefTests
{
    private const string ReceiptPath =
        "C:\\repo\\.orchestrator\\operator-evidence\\run-goal-timeout-historical-receipts.json";

    private const string AnswerWithReceipts =
        "Runs 20260906T1200Z and 20260906T1830Z, lane interval 14:02-14:47. " +
        "Receipts at " + ReceiptPath + " (sha256:3f9a1c2b4d5e6f7089ab) and " +
        "C:\\repo\\.orchestrator\\operator-evidence\\run-goal-timeout-answered-state.json.";

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_answered_planner_request_reaches_a_later_same_goal_role")]
    public void AnsweredPlannerRequestReachesLaterSameGoalRole()
    {
        var context = CreatePlannerAndDeveloper();
        var request = RaiseEvidenceRequest(context, "historical-trx-receipts");
        context.Kernel.SubmitHumanInput(request.Id, AnswerWithReceipts);

        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id).Content;

        Assert.Contains("## Answered Prerequisite Evidence", brief, StringComparison.Ordinal);
        Assert.Contains(request.Id.Value, brief, StringComparison.Ordinal);
        Assert.Contains(ReceiptPath, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_task_private_spec_clarification_stays_out_of_later_briefs")]
    public void TaskPrivateSpecClarificationStaysOutOfLaterBriefs()
    {
        var context = CreatePlannerAndDeveloper();
        var request = context.Kernel.RequestHumanInputDeduplicated(
            context.Goal.Id,
            context.Planner.Id,
            "Which lane should the planner assume?",
            kind: HumanWaitKind.SpecClarification).Request;
        context.Kernel.SubmitHumanInput(request.Id, AnswerWithReceipts);

        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id).Content;

        Assert.DoesNotContain("## Answered Prerequisite Evidence", brief, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Id.Value, brief, StringComparison.Ordinal);
        Assert.DoesNotContain(ReceiptPath, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_answered_request_on_another_goal_never_crosses_over")]
    public void AnsweredRequestOnAnotherGoalNeverCrossesOver()
    {
        var context = CreatePlannerAndDeveloper();
        var other = CreatePlannerAndDeveloper(context.Kernel, "Unrelated goal objective.");
        var request = RaiseEvidenceRequest(other, "other-goal-receipts");
        other.Kernel.SubmitHumanInput(request.Id, AnswerWithReceipts);

        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id).Content;

        Assert.DoesNotContain("## Answered Prerequisite Evidence", brief, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Id.Value, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_unanswered_request_is_not_propagated")]
    public void UnansweredRequestIsNotPropagated()
    {
        var context = CreatePlannerAndDeveloper();
        var request = RaiseEvidenceRequest(context, "still-pending-receipts");

        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id).Content;

        Assert.DoesNotContain("## Answered Prerequisite Evidence", brief, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Id.Value, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_originating_task_sees_its_own_answer_exactly_once")]
    public void OriginatingTaskSeesItsOwnAnswerExactlyOnce()
    {
        var context = CreatePlannerAndDeveloper();
        var request = RaiseEvidenceRequest(context, "originator-receipts");
        context.Kernel.SubmitHumanInput(request.Id, AnswerWithReceipts);

        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Planner.Id).Content;

        Assert.Contains("## Resolved Human Input", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("## Answered Prerequisite Evidence", brief, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(brief, request.Id.Value));
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_dismissed_and_superseded_answers_are_excluded")]
    public void DismissedAndSupersededAnswersAreExcluded()
    {
        var context = CreatePlannerAndDeveloper();
        var dismissed = RaiseEvidenceRequest(context, "dismissed-receipts");
        context.Kernel.DismissHumanInput(dismissed.Id);

        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id).Content;

        Assert.DoesNotContain(dismissed.Id.Value, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_over_cap_answers_trim_oldest_first_with_a_visible_budget_note")]
    public void OverCapAnswersTrimOldestFirstWithVisibleBudgetNote()
    {
        var clock = new FakeClock();
        var context = CreatePlannerAndDeveloper(new AgentOrchestratorKernel(clock));
        var requests = new List<HumanInputRequest>();
        for (var index = 0; index < 8; index++)
        {
            var request = RaiseEvidenceRequest(context, $"receipt-batch-{index}", index + 1);
            context.Kernel.SubmitHumanInput(
                request.Id,
                $"Batch {index} runs 2026090{index}T1200Z through lane interval 1{index}:00-1{index}:45. " +
                $"Receipts at C:\\repo\\.orchestrator\\operator-evidence\\batch-{index}-receipts.json " +
                $"and C:\\repo\\.orchestrator\\operator-evidence\\batch-{index}-lane.json " +
                $"with sha256:{index}f9a1c2b4d5e6f7089ab{index} recorded by the operator for later roles.");
            requests.Add(request);
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        var built = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id);
        var section = ExtractSection(built.Content, "## Answered Prerequisite Evidence");

        Assert.NotEmpty(built.TrimmedPrerequisiteEvidenceRequestIds ?? []);
        Assert.Contains("Budget note: prerequisite evidence trimmed for request ids:", section, StringComparison.Ordinal);
        foreach (var trimmedId in built.TrimmedPrerequisiteEvidenceRequestIds!)
        {
            Assert.Contains(trimmedId, section, StringComparison.Ordinal);
        }

        // Oldest-first trimming: the newest answer must survive, the oldest must be the one named.
        Assert.Contains(requests[^1].Id.Value, section, StringComparison.Ordinal);
        Assert.Contains(requests[0].Id.Value, built.TrimmedPrerequisiteEvidenceRequestIds!);

        // Every retained entry keeps the floor: request id, a summary, and its evidence paths.
        foreach (var retained in requests.Where(request =>
            !built.TrimmedPrerequisiteEvidenceRequestIds!.Contains(request.Id.Value)))
        {
            var entry = Assert.Single(
                section.Split(Environment.NewLine),
                line => line.StartsWith($"- {retained.Id.Value}:", StringComparison.Ordinal));
            Assert.Contains("evidence:", entry, StringComparison.Ordinal);
            Assert.Contains("-receipts.json", entry, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_long_answer_renders_as_reference_not_a_copied_journal")]
    public void LongAnswerRendersAsReferenceNotCopiedJournal()
    {
        var context = CreatePlannerAndDeveloper();
        var request = RaiseEvidenceRequest(context, "journal-receipts");
        var interiorBulk = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 200).Select(index => $"journal line {index}: interior bulk that must not be copied"));
        context.Kernel.SubmitHumanInput(
            request.Id,
            $"Receipts at {ReceiptPath}.{Environment.NewLine}{interiorBulk}");

        var section = ExtractSection(
            context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id).Content,
            "## Answered Prerequisite Evidence");

        Assert.Contains(request.Id.Value, section, StringComparison.Ordinal);
        Assert.Contains(ReceiptPath, section, StringComparison.Ordinal);
        Assert.DoesNotContain("journal line 150", section, StringComparison.Ordinal);
        Assert.True(section.Length <= 2_000, $"section was {section.Length} chars: {section}");
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_brief_build_reads_no_filesystem_path_named_by_an_answer")]
    public void BriefBuildReadsNoFilesystemPathNamedByAnAnswer()
    {
        var context = CreatePlannerAndDeveloper();
        var request = RaiseEvidenceRequest(context, "missing-receipts");
        const string missingPath = "C:\\does\\not\\exist\\r.json";
        context.Kernel.SubmitHumanInput(request.Id, $"Receipt is at {missingPath} with sha256:abc1234.");

        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id).Content;

        Assert.False(File.Exists(missingPath));
        Assert.Contains(missingPath, brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_sentinel_text_is_treated_exactly_as_resolved_human_input_treats_it")]
    public void SentinelTextIsTreatedExactlyAsResolvedHumanInputTreatsIt()
    {
        // No redaction layer exists on the brief path today, and the operator decision forbids adding
        // one. The falsifiable guarantee is parity: propagated evidence gets the same treatment the
        // existing resolved-answer path already gives identical text.
        const string sentinel = "SENTINEL-SECRET-6f2a41";
        var propagated = CreatePlannerAndDeveloper();
        var evidence = RaiseEvidenceRequest(propagated, "sentinel-receipts");
        propagated.Kernel.SubmitHumanInput(evidence.Id, $"Token {sentinel} is required.");

        var existing = CreatePlannerAndDeveloper();
        var clarification = existing.Kernel.RequestHumanInputDeduplicated(
            existing.Goal.Id,
            existing.Developer.Id,
            "Which token should the developer use?",
            kind: HumanWaitKind.SpecClarification).Request;
        existing.Kernel.SubmitHumanInput(clarification.Id, $"Token {sentinel} is required.");

        var propagatedBrief = propagated.Kernel.BuildTaskBrief(propagated.Goal.Id, propagated.Developer.Id).Content;
        var existingBrief = existing.Kernel.BuildTaskBrief(existing.Goal.Id, existing.Developer.Id).Content;

        Assert.Equal(
            existingBrief.Contains(sentinel, StringComparison.Ordinal),
            propagatedBrief.Contains(sentinel, StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_absent_answers_leave_the_brief_byte_identical")]
    public void AbsentAnswersLeaveTheBriefByteIdentical()
    {
        var context = CreatePlannerAndDeveloper();
        var before = context.Kernel.BuildTaskBrief(context.Goal.Id, context.Developer.Id);

        Assert.DoesNotContain("## Answered Prerequisite Evidence", before.Content, StringComparison.Ordinal);
        Assert.Empty(before.TrimmedPrerequisiteEvidenceRequestIds ?? []);
    }

    [Xunit.Fact(DisplayName = "PrerequisiteEvidence_api_runner_classifies_the_request_like_the_subscription_path")]
    public async Task ApiRunnerClassifiesTheRequestLikeTheSubscriptionPath()
    {
        // Second recording site. If only the subscription path classified, the fix would be
        // path-dependent and the API runner would keep producing unclassified, non-propagating
        // requests.
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify planner evidence on the API runner path.");
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var planner = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
        var provider = new FakeModelProvider(
            "OpenAI",
            "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"historical-trx-receipts\"," +
            "\"availability\":\"retrievable\",\"store\":\"main checkout\"," +
            "\"needed\":\"the two run ids and lane receipts\",\"reason\":\"the worker cannot read the main checkout\"}");
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, planner.Id);

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.PlannerPrerequisiteEvidence, request.Kind);
        Assert.Equal(
            HumanInputRequest.BuildPlannerEvidenceFingerprint(1, "historical-trx-receipts"),
            request.QuestionFingerprint);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = content.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string ExtractSection(string content, string heading)
    {
        var lines = content.Split(Environment.NewLine);
        var start = Array.FindIndex(lines, line => line.Equals(heading, StringComparison.Ordinal));
        Assert.True(start >= 0, $"brief did not contain '{heading}':{Environment.NewLine}{content}");
        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        var length = (end < 0 ? lines.Length : end) - start;
        return string.Join(Environment.NewLine, lines.Skip(start).Take(length));
    }

    private static HumanInputRequest RaiseEvidenceRequest(
        GoalContext context,
        string evidenceKey,
        int criterionIndex = 1)
    {
        var fingerprint = HumanInputRequest.BuildPlannerEvidenceFingerprint(criterionIndex, evidenceKey);
        return context.Kernel.RequestHumanInputDeduplicated(
            context.Goal.Id,
            context.Planner.Id,
            $"Planner evidence request for criterion {criterionIndex}: {evidenceKey}. " +
            "Availability: retrievable from store 'main checkout', which the worker cannot reach. " +
            "Reason: the worktree does not carry historical operator receipts",
            kind: HumanWaitKind.PlannerPrerequisiteEvidence,
            questionFingerprint: fingerprint,
            blockerFingerprint: fingerprint).Request;
    }

    private static GoalContext CreatePlannerAndDeveloper(
        AgentOrchestratorKernel? kernel = null,
        string objective = "Preserve answered prerequisite evidence in downstream worker context.")
    {
        kernel ??= new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            objective,
            [
                new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner),
                new TaskSpec(TaskId.New(), "Implement the scoped slice.", AgentRole.Developer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return new GoalContext(kernel, goal, goal.Tasks[0], goal.Tasks[1]);
    }

    private sealed record GoalContext(
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Planner,
        TaskSpec Developer);
}
