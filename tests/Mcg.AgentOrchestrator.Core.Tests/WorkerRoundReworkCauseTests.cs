using Mcg.AgentOrchestrator.Core;

public sealed class WorkerRoundReworkCauseTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly (string Message, ReworkCauseFamily Family)[] PrefixCases =
    [
        ("auto-review-retry round 2 convergence brief: finding", ReworkCauseFamily.ReviewFinding),
        ("auto-review-retry round 2: finding", ReworkCauseFamily.ReviewFinding),
        ("review-finding contract-repair: missing field", ReworkCauseFamily.ReviewContractRepair),
        ("finding evidence-on-demand: receipt", ReworkCauseFamily.EvidenceRerun),
        ("reviewer evidence-on-demand: receipt", ReworkCauseFamily.EvidenceRerun),
        ("ACTIONABLE_CANDIDATE_RED: source", ReworkCauseFamily.CandidateRed),
        ("DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE: red", ReworkCauseFamily.CandidateRed),
        ("DEFERRED_NO_CHANGE_EVIDENCE_UNAVAILABLE: missing", ReworkCauseFamily.CandidateRed),
        ("DEFERRED_NO_CHANGE_REPEAT_RED: red", ReworkCauseFamily.CandidateRed),
        ("Acceptance criteria unmet; retrying task with feedback", ReworkCauseFamily.GateRed),
        ("Auto-retry sandbox-preflight dispatch flake", ReworkCauseFamily.FlakeOrApparatus),
        ("Auto-retry verification-inconclusive Tester task", ReworkCauseFamily.FlakeOrApparatus),
        ("pre-review build repair: compiler error", ReworkCauseFamily.FlakeOrApparatus),
        ("Dispatch hit a recoverable subscription usage limit", ReworkCauseFamily.Environment),
        ("Dispatch hit provider connectivity failure", ReworkCauseFamily.Environment),
        ("Dispatch hit ProviderInterruption", ReworkCauseFamily.Environment),
        ("Auto-requeued interrupted dispatch after conductor loop stop.", ReworkCauseFamily.Environment),
        ("Invalidated Reviewer task because upstream Developer changed", ReworkCauseFamily.DownstreamRerun),
        ("Invalidated Tester task because retried upstream Developer", ReworkCauseFamily.DownstreamRerun),
        ("Invalidated superseded Reviewer verdict because upstream changed", ReworkCauseFamily.DownstreamRerun),
        ("missing-planner-artifact: dependency reroute to Planner", ReworkCauseFamily.Routing)
    ];

    [Fact]
    public void ProjectsFirstPassAndEveryPrefixFamilyPlusClarification()
    {
        var tasks = PrefixCases.Select((_, index) => Task($"task-{index}")).Append(Task("clarification")).ToArray();
        var events = PrefixCases.Select((item, index) =>
            Event($"task-{index}", 2, ProgressKind.TaskRetried, item.Message))
            .Append(Event("clarification", 2, ProgressKind.HumanInputReceived, "answered")).ToArray();
        var rounds = WorkerRoundLedger.FromGoal(GoalWith(tasks, events));

        Assert.Equal(tasks.Length * 2, rounds.Count);
        Assert.All(rounds.Where(r => r.RoundIndex == 1), r => Assert.Equal(ReworkCauseFamily.FirstPass, r.ReworkCause));
        for (var index = 0; index < PrefixCases.Length; index++)
            Assert.Equal(PrefixCases[index].Family,
                Assert.Single(rounds.Where(r => r.TaskId == $"task-{index}" && r.RoundIndex == 2)).ReworkCause);
        Assert.Equal(ReworkCauseFamily.Clarification,
            Assert.Single(rounds.Where(r => r.TaskId == "clarification" && r.RoundIndex == 2)).ReworkCause);
    }

    [Theory]
    [InlineData(OperatorActorKind.Human, ReworkCauseFamily.OperatorRetry)]
    [InlineData(OperatorActorKind.Agent, ReworkCauseFamily.StewardRoute)]
    [InlineData(null, ReworkCauseFamily.Unclassified)]
    public void FreeTextRetryUsesAppliedActorOrRemainsUnclassified(OperatorActorKind? actor, ReworkCauseFamily expected)
    {
        var goal = GoalWith([Task("task", [Receipt(RetryCause.Unknown)])],
            [Event("task", 2, ProgressKind.TaskRetried, "please try a different approach")]);
        AppliedRetryIntent[] intents = actor is { } kind ? [new("task", Start.AddHours(2), kind)] : [];

        var rounds = WorkerRoundLedger.FromGoal(goal, intents);

        Assert.Equal(ReworkCauseFamily.FirstPass, rounds[0].ReworkCause);
        Assert.Equal(expected, rounds[1].ReworkCause);
        Assert.Equal(actor is null ? ReworkCauseFamily.Unclassified : expected,
            WorkerRoundLedger.FromGoals([goal], intents)[1].ReworkCause);
    }

    [Fact]
    public void IntentJoinUsesTaskAndStrictLowerInclusiveUpperOrderingWithNewestMatch()
    {
        var retryAt = Start.AddHours(2);
        var dispatchAt = Start.AddHours(3);
        var goal = GoalWith([Task("task")], [Event("task", 2, ProgressKind.TaskRetried, "free text")]);
        AppliedRetryIntent[] excluded =
        [
            new("other", dispatchAt, OperatorActorKind.Human),
            new("task", Start.AddHours(1), OperatorActorKind.Human),
            new("task", dispatchAt.AddTicks(1), OperatorActorKind.Human)
        ];
        Assert.Equal(ReworkCauseFamily.Unclassified, WorkerRoundLedger.FromGoal(goal, excluded)[1].ReworkCause);
        Assert.Equal(ReworkCauseFamily.OperatorRetry, WorkerRoundLedger.FromGoal(goal,
            [.. excluded, new("task", Start.AddHours(1).AddTicks(1), OperatorActorKind.Human)])[1].ReworkCause);
        Assert.Equal(ReworkCauseFamily.OperatorRetry, WorkerRoundLedger.FromGoal(goal,
            [.. excluded, new("task", retryAt.AddTicks(1), OperatorActorKind.Human)])[1].ReworkCause);
        Assert.Equal(ReworkCauseFamily.StewardRoute, WorkerRoundLedger.FromGoal(goal,
            [.. excluded, new("task", dispatchAt, OperatorActorKind.Agent),
                new("task", retryAt.AddTicks(1), OperatorActorKind.Human)])[1].ReworkCause);
    }

    [Theory]
    [InlineData(OperatorActorKind.Human, ReworkCauseFamily.OperatorRetry)]
    [InlineData(OperatorActorKind.Agent, ReworkCauseFamily.StewardRoute)]
    public void LiveRetryThenIntentCompletionThenDispatchUsesAppliedActor(
        OperatorActorKind actor, ReworkCauseFamily expected)
    {
        var goal = GoalWith([Task("task", [Receipt(RetryCause.Unknown)])],
            [Event("task", 2, ProgressKind.TaskRetried, "free-text retry from live store")]);
        AppliedRetryIntent[] intents = [new("task", Start.AddHours(2.5), actor)];

        Assert.Equal(expected, WorkerRoundLedger.FromGoal(goal, intents)[1].ReworkCause);
    }

    [Fact]
    public void NewestRetryWinsOverOlderPrefixesClarificationIntentsAndReceipt()
    {
        var goal = GoalWith([Task("task", [Receipt(RetryCause.ProviderInterruption)])],
            [Event("task", 2.5, ProgressKind.TaskRetried, "review-finding contract-repair: latest"),
             Event("task", 2, ProgressKind.TaskRetried, "auto-review-retry round 2: older"),
             Event("task", 2.75, ProgressKind.HumanInputReceived, "answered")]);
        Assert.Equal(ReworkCauseFamily.ReviewContractRepair, WorkerRoundLedger.FromGoal(goal,
            [new("task", Start.AddHours(2.5), OperatorActorKind.Agent)])[1].ReworkCause);

        var freeText = GoalWith([Task("task")],
            [Event("task", 2, ProgressKind.TaskRetried, "auto-review-retry round 2: older"),
             Event("task", 2.5, ProgressKind.TaskRetried, "latest unrecognized"),
             Event("task", 2.75, ProgressKind.HumanInputReceived, "answered")]);
        Assert.Equal(ReworkCauseFamily.Unclassified, WorkerRoundLedger.FromGoal(freeText)[1].ReworkCause);
    }

    [Theory]
    [InlineData(1, ReworkCauseFamily.Unclassified)]
    [InlineData(3, ReworkCauseFamily.GateRed)]
    [InlineData(4, ReworkCauseFamily.Unclassified)]
    public void RetryEventBoundsAreStrictAfterPreviousAndInclusiveAtCurrent(int hour, ReworkCauseFamily expected)
    {
        var goal = GoalWith([Task("task")],
            [Event("task", hour, ProgressKind.TaskRetried, "Acceptance criteria unmet; retrying task with feedback"),
             Event("other", 2, ProgressKind.TaskRetried, "auto-review-retry round 2: unrelated")]);
        Assert.Equal(expected, WorkerRoundLedger.FromGoal(goal)[1].ReworkCause);
    }

    [Fact]
    public void ThirdRoundUsesItsOwnGapAndClarificationRequiresTaskAndWindow()
    {
        var task = Task("task") with { DispatchHistory = [Dispatch(5), Dispatch(1), Dispatch(3)] };
        var goal = GoalWith([task],
            [Event("task", 2, ProgressKind.TaskRetried, "finding evidence-on-demand: second"),
             Event("task", 4, ProgressKind.HumanInputReceived, "third")]);
        Assert.Equal([ReworkCauseFamily.FirstPass, ReworkCauseFamily.EvidenceRerun, ReworkCauseFamily.Clarification],
            WorkerRoundLedger.FromGoal(goal).Select(r => r.ReworkCause));

        var unrelated = GoalWith([Task("task")],
            [Event("other", 2, ProgressKind.HumanInputReceived, "other task"),
             Event("task", 1, ProgressKind.HumanInputReceived, "lower boundary"),
             Event("task", 4, ProgressKind.HumanInputReceived, "after dispatch")]);
        Assert.Equal(ReworkCauseFamily.Unclassified, WorkerRoundLedger.FromGoal(unrelated)[1].ReworkCause);
    }

    [Theory]
    [InlineData(RetryCause.Unknown, ReworkCauseFamily.Unclassified)]
    [InlineData(RetryCause.ProviderInterruption, ReworkCauseFamily.Environment)]
    [InlineData(RetryCause.ProviderBudgetRecovery, ReworkCauseFamily.Environment)]
    [InlineData(RetryCause.MainDriftConflict, ReworkCauseFamily.Routing)]
    [InlineData(RetryCause.EnvironmentApparatusFailure, ReworkCauseFamily.FlakeOrApparatus)]
    [InlineData(RetryCause.NewSourceFinding, ReworkCauseFamily.Unclassified)]
    public void UnmatchedRetryFallsBackToLinkedReceipt(RetryCause cause, ReworkCauseFamily expected)
    {
        var goal = GoalWith([Task("task", [Receipt(cause)])],
            [Event("task", 2, ProgressKind.TaskRetried, "unrecognized")]);
        Assert.Equal(expected, WorkerRoundLedger.FromGoal(goal)[1].ReworkCause);
        var withoutRetry = GoalWith([Task("task", [Receipt(cause)])], []);
        Assert.Equal(expected, WorkerRoundLedger.FromGoal(withoutRetry)[1].ReworkCause);
    }

    [Fact]
    public void ReceiptFallbackIgnoresOtherDispatchAndUsesNewestKnownCause()
    {
        var goal = GoalWith([Task("task",
            [Receipt(RetryCause.MainDriftConflict) with { RecordedAt = Start.AddHours(2.75) },
             Receipt(RetryCause.ProviderInterruption) with { RecordedAt = Start.AddHours(2.5) },
             Receipt(RetryCause.Unknown) with { RecordedAt = Start.AddHours(3) },
             Receipt(RetryCause.EnvironmentApparatusFailure) with { LinkedDispatchAt = Start.AddHours(4) }])], []);
        Assert.Equal(ReworkCauseFamily.Routing, WorkerRoundLedger.FromGoal(goal)[1].ReworkCause);
    }

    private static TaskSnapshot Task(string id, IReadOnlyList<RetryAdmissionReceipt>? receipts = null) =>
        new(id, "Work", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null,
            DispatchHistory: [Dispatch(1), Dispatch(3)], RetryAdmissionHistory: receipts);

    private static TaskDispatchSnapshot Dispatch(int hour) => new("worker", "command", "root", Start.AddHours(hour));

    private static ProgressEventSnapshot Event(string taskId, double hour, ProgressKind kind, string message) =>
        new("goal", taskId, kind, message, Start.AddHours(hour));

    private static RetryAdmissionReceipt Receipt(RetryCause cause) => new("receipt", cause,
        new RetryContextFingerprint(1, "fingerprint"), RetryAdmissionDecision.Allowed,
        RetryAdmissionRoute.SameRole, PaidRouteClassification.Paid, Start.AddHours(3), Start.AddHours(2.5));

    private static Goal GoalWith(IReadOnlyList<TaskSnapshot> tasks, IReadOnlyList<ProgressEventSnapshot> timeline) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal", "Work", GoalStatus.Active, tasks, timeline)], [])).Goals.Single();
}
