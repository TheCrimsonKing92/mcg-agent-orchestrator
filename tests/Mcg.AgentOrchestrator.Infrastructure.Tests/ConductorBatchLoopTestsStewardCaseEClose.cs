using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each harness owns its stores and clock; process execution is injected.
public sealed class ConductorBatchLoopTestsStewardCaseEClose
{
    private const string Diagnosis = "The explanation answers the format finding; request another Reviewer judgment.";

    [Xunit.Fact]
    public async Task Sufficient_explanation_enqueues_close_then_verbatim_Reviewer_retry()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);

        var intents = await OrderedIntents(harness);
        Assert.Equal(2, intents.Length);
        var close = Payload(intents[0]);
        var retry = Payload(intents[1]);
        Assert.Equal("close", close.Shape);
        Assert.Equal(harness.Developer.Id.Value, intents[0].TaskId);
        Assert.Contains(StewardCaseEHarness.WorkerResult, close.Text, StringComparison.Ordinal);
        Assert.Contains(Diagnosis, close.Text, StringComparison.Ordinal);
        Assert.Equal("route", retry.Shape);
        Assert.Equal(harness.Reviewer.Id.Value, intents[1].TaskId);
        Assert.Equal(nameof(RetryCause.ContractClarification), retry.Cause);
        Assert.Contains(StewardCaseEHarness.WorkerResult, retry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Diagnosis, retry.Text, StringComparison.Ordinal);
        Assert.Contains($"candidate {StewardCaseEHarness.Sha} is unchanged", retry.Text, StringComparison.Ordinal);
        Assert.True(intents[0].CreatedAt < intents[1].CreatedAt); // Injected clock, persisted apply ordering.
        Assert.NotEqual(intents[0].IdempotencyKey, intents[1].IdempotencyKey);
        Assert.Null(retry.Precondition);
        Assert.Contains($"developer-close-intent={intents[0].Id}", retry.EvidenceReferences);
        Assert.All(intents, intent =>
        {
            Assert.Equal("steward", intent.Actor);
            Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
            Assert.Equal("conductor-steward", intent.Channel);
            Assert.Equal(OperatorIntentAdjudication.StewardAssurance, intent.AuthenticationAssurance);
        });
        Assert.Equal(WorkTaskStatus.Failed, harness.Developer.Status);
    }

    [Xunit.Fact]
    public async Task Applied_close_completes_Developer_then_Reviewer_gets_new_input()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);
        var intents = await OrderedIntents(harness);
        Assert.Equal(2, intents.Length);
        Assert.False(ConductorStewardCaseEAdmission.AdmitsReviewerRetry(harness.Goal, harness.Reviewer,
            Payload(intents[1]), harness.Head));

        ApplyNext(harness);

        Assert.Equal(WorkTaskStatus.Completed, harness.Developer.Status);
        Assert.True(harness.Developer.LastVerification?.Succeeded);
        Assert.Contains(StewardCaseEHarness.WorkerResult, harness.Developer.LastVerification!.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Intents.GetAsync(intents[0].Id))!.Status);
        Assert.Equal(OperatorIntentStatus.Pending, (await harness.Intents.GetAsync(intents[1].Id))!.Status);
        Assert.True(ConductorStewardCaseEAdmission.AdmitsReviewerRetry(harness.Goal, harness.Reviewer,
            Payload(intents[1]), harness.Head));
        Assert.Equal("needs-work", UnchangedCandidateRule.Evaluate(
            harness.Goal, harness.Reviewer, StewardCaseEHarness.Candidate)?.PriorVerdict);

        ApplyNext(harness);

        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Intents.GetAsync(intents[1].Id))!.Status);
        var retry = harness.Goal.Timeline.Last(item => item.TaskId == harness.Reviewer.Id && item.Kind == ProgressKind.TaskRetried);
        Assert.Contains(StewardCaseEHarness.WorkerResult, retry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Diagnosis, retry.Message, StringComparison.Ordinal);
        Assert.False(harness.Reviewer.LatestRetryInherited);
        Assert.Equal(harness.Reviewer.LatestRetryAt, harness.Reviewer.LatestRoleInputRetryAt);
        Assert.Equal(RetryCause.ContractClarification, harness.Reviewer.PendingRetryCause);
        Assert.Contains(StewardCaseEHarness.WorkerResult, harness.Reviewer.AcceptedRetryFeedback?.Message, StringComparison.Ordinal);
        Assert.Null(UnchangedCandidateRule.Evaluate(harness.Goal, harness.Reviewer, StewardCaseEHarness.Candidate));
        Assert.Empty(harness.Index.Read()); // Case E is not an apparatus re-gate.
    }

    [Xunit.Fact]
    public async Task Insufficient_explanation_raises_one_owner_question_without_intents()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        var question = $"Developer task {harness.Developer.Id.Value} has not answered the WORKER_RESULT format finding.";
        harness.Model.Reply(JsonSerializer.Serialize(new { kind = "ask-owner", question, evidenceReferences = new[] { "finding=format" } }));

        await Harvest(harness);
        harness.Host.ServiceTick(harness.Kernel);

        Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Contains(question, harness.Goal.CurrentHold?.Blocker, StringComparison.Ordinal);
        Assert.Contains("case=E", harness.Goal.CurrentHold?.Blocker, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, harness.Developer.Status);
        Assert.Equal(1, harness.Model.Calls);
        Assert.Equal(1, OwnerQuestionCount(harness));
    }

    [Xunit.Theory]
    [Xunit.InlineData("route", "disallowed-action")]
    [Xunit.InlineData("reopen-regate", "disallowed-action")]
    [Xunit.InlineData("close", "evidence-reference-unresolved")]
    public async Task Unsafe_proposal_asks_owner_and_submits_neither_intent(string kind, string reason)
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        harness.Model.Reply(JsonSerializer.Serialize(new
        {
            kind, targetTaskId = harness.Developer.Id.Value, text = Diagnosis, instruction = "instruction",
            cause = "ContractClarification", reversibility = "reversible",
            evidenceReferences = new[] { kind == "close" ? "trx:missing.trx" : "finding=format" }
        }));

        await Harvest(harness);

        Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Contains(reason, harness.Goal.CurrentHold?.Blocker, StringComparison.Ordinal);
        Assert.Equal(1, OwnerQuestionCount(harness));
    }

    [Xunit.Fact]
    public async Task Unresolvable_finding_Reviewer_asks_owner_without_routing()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed(retryMessage: harness.RetryMessage.Replace(harness.Reviewer.Id.Value[..8], "00000000"));
        ReplyClose(harness);

        await Harvest(harness);

        Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.Contains("reviewer-task-unresolved", harness.Goal.CurrentHold?.Blocker, StringComparison.Ordinal);
        Assert.Equal(1, OwnerQuestionCount(harness));
    }

    [Xunit.Fact]
    public async Task Repeated_round_on_same_candidate_asks_owner_after_one_action()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);
        ApplyNext(harness);
        ApplyNext(harness);
        harness.Host.ServiceTick(harness.Kernel); // Reconcile the paired route into the existing action bound.
        harness.Seed();

        harness.Host.ServiceTick(harness.Kernel);
        harness.Host.ServiceTick(harness.Kernel);

        Assert.Equal(2, (await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)).Count);
        Assert.Equal(1, harness.Model.Calls);
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Equal(1, OwnerQuestionCount(harness));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Head_change_before_apply_refuses_Reviewer_retry(bool afterClose)
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);
        var intents = await OrderedIntents(harness);
        Assert.Equal(2, intents.Length);
        if (afterClose) ApplyNext(harness);
        var reviewerRetryAt = harness.Reviewer.LatestRetryAt;
        harness.Head = StewardCaseEHarness.OtherSha;

        harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);
        if (!afterClose) harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.Equal(afterClose ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, harness.Developer.Status);
        Assert.Equal(reviewerRetryAt, harness.Reviewer.LatestRetryAt);
        Assert.Equal(OperatorIntentStatus.Rejected, (await harness.Intents.GetAsync(intents[1].Id))!.Status);
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intents[1].Id}");
        Assert.Equal("steward-capability-boundary", decision?.Effect?.Result);
    }

    [Xunit.Fact]
    public async Task Apply_rechecks_case_E_head_after_recording_decision()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);
        var calls = 0;
        var coordinator = harness.CreateCoordinator(_ => ++calls == 1 ? StewardCaseEHarness.Sha : StewardCaseEHarness.OtherSha);

        coordinator.ExecutePending(harness.Kernel, harness.Goal);

        Assert.Equal(2, calls);
        Assert.Equal(WorkTaskStatus.Failed, harness.Developer.Status);
        Assert.Empty(harness.Index.Read());
    }

    [Xunit.Fact]
    public async Task Refused_Reviewer_retry_keeps_the_applied_close_in_the_action_bound()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);
        ApplyNext(harness);
        harness.Head = StewardCaseEHarness.OtherSha;
        harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);
        harness.Host.ServiceTick(harness.Kernel);
        harness.Head = StewardCaseEHarness.Sha;
        harness.Seed();

        harness.Host.ServiceTick(harness.Kernel);

        Assert.Equal(2, (await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)).Count);
        Assert.Equal(1, harness.Model.Calls);
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Equal(1, OwnerQuestionCount(harness));
    }

    [Xunit.Theory]
    [Xunit.InlineData("developer-close-intent=", "missing-close")]
    [Xunit.InlineData("developer-dispatch=", "0")]
    [Xunit.InlineData("dispatch-base=", StewardCaseEHarness.OtherSha)]
    public async Task Reviewer_retry_requires_the_paired_close_and_dispatch(string prefix, string value)
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        ReplyClose(harness);
        await Harvest(harness);
        var intents = await OrderedIntents(harness);
        Assert.Equal(2, intents.Length);
        var retry = Payload(intents[1]);
        ApplyNext(harness);
        Assert.True(ConductorStewardCaseEAdmission.AdmitsReviewerRetry(harness.Goal, harness.Reviewer, retry, harness.Head));

        var tampered = retry with
        {
            EvidenceReferences = retry.EvidenceReferences.Select(reference =>
                reference.StartsWith(prefix, StringComparison.Ordinal) ? prefix + value : reference).ToArray()
        };

        Assert.False(ConductorStewardCaseEAdmission.AdmitsReviewerRetry(harness.Goal, harness.Reviewer, tampered, harness.Head));
    }

    [Xunit.Fact]
    public async Task Case_E_prompt_keeps_Reviewer_as_judge_of_resolution()
    {
        using var harness = new StewardCaseEHarness();
        harness.Seed();
        string? prompt = null;
        var round = new ClaudeConductorStewardModelRound(Path.Combine(harness.Root, "receipts"),
            (request, _) =>
            {
                prompt = request.StandardInput;
                return Task.FromResult(new WorkerProcessRunResult(0, "{\"kind\":\"no-action\"}", ""));
            }, new EmptyFiles(), (_, _) => true);

        await round.DispatchAsync(Assert.Single(harness.Detector.Detect(harness.Goal)), harness.Root, CancellationToken.None);

        Assert.NotNull(prompt);
        Assert.Contains("Allowed kinds: close, ask-owner, no-action", prompt, StringComparison.Ordinal);
        Assert.Contains("judge only whether the Developer's no-change explanation answers the Reviewer finding", prompt, StringComparison.Ordinal);
        Assert.Contains("The Reviewer remains the judge of whether the finding is resolved", prompt, StringComparison.Ordinal);
        Assert.Contains("If insufficient return ask-owner", prompt, StringComparison.Ordinal);
    }

    private static void ReplyClose(StewardCaseEHarness harness) => harness.Model.Reply(JsonSerializer.Serialize(new
    {
        kind = "close", targetTaskId = harness.Developer.Id.Value, text = Diagnosis,
        evidenceReferences = new[] { "finding=format" }
    }));

    private static async Task Harvest(StewardCaseEHarness harness)
    {
        harness.Host.ServiceTick(harness.Kernel);
        Assert.NotNull(harness.Host.CurrentRound);
        try { await harness.Host.CurrentRound!.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new InvalidOperationException("Steward case E model round did not complete.", ex); }
        Assert.Equal(1, harness.Model.Calls);
        harness.Host.ServiceTick(harness.Kernel);
    }

    private static void ApplyNext(StewardCaseEHarness harness)
    {
        Assert.True(harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal).MutatedGoalState);
        harness.Coordinator.CompletePersisted([harness.Goal.Id]);
    }

    private static async Task<OperatorIntentRecord[]> OrderedIntents(StewardCaseEHarness harness) =>
        (await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)).OrderBy(intent => intent.CreatedAt)
            .ThenBy(intent => intent.Id, StringComparer.Ordinal).ToArray();

    private static AdjudicateOperatorIntentPayload Payload(OperatorIntentRecord intent) =>
        JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;

    private static int OwnerQuestionCount(StewardCaseEHarness harness) =>
        File.ReadAllLines(Path.Combine(harness.Root, "conduct-events.log")).Count(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.GetProperty("eventKind").GetString() == "goal-escalation";
        });

    private sealed class EmptyFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => [];
    }
}
