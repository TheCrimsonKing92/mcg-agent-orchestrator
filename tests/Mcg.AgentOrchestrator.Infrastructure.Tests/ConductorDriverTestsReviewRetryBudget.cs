using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsReviewRetryBudget
{
    [Xunit.Fact]
    public void A_Closing_one_and_opening_one_restarts_consecutive_budget()
    {
        var scenario = new Scenario();
        for (var round = 1; round <= 9; round++)
        {
            scenario.Review(ShrinkingRound(round));
            scenario.AssertRetried(1);
            if (round > 1) Assert.Contains($"review_budget_reset=open-set-shrank closed=F-{round - 1}", scenario.Message);
        }
    }

    [Xunit.Fact]
    public void B_Closing_one_of_ten_restarts_consecutive_budget()
    {
        var scenario = new Scenario();
        for (var round = 1; round <= 9; round++)
        {
            scenario.Review(Enumerable.Range(1, 10).Select(id => ShrinkFinding(id,
                id < round ? ReviewFindingState.Resolved : ReviewFindingState.Open)).ToArray());
            scenario.AssertRetried(1);
            if (round > 1) Assert.Contains($"review_budget_reset=open-set-shrank closed=F-{round - 1}", scenario.Message);
        }
    }

    [Xunit.Fact]
    public void C_One_shrink_then_repetition_stops_on_eighth_round()
    {
        var scenario = new Scenario();
        for (var round = 1; round <= 8; round++)
        {
            scenario.Review(round == 1 ? [Repeat("A")] : [Repeat("A", ReviewFindingState.Resolved), Repeat("B")]);
            if (round < 8) scenario.AssertRetried(round == 1 ? 1 : round - 1);
            else
            {
                scenario.AssertStopped("auto-review-retry stopped at review round 7/7");
                Assert.Contains("surviving_stable_ids=F-B", scenario.Escalation);
                AssertOperatorActions(scenario);
            }
        }
    }

    [Xunit.Fact]
    public void D_Unchanged_finding_stops_on_seventh_round()
    {
        var scenario = AtConsecutiveCap();
        scenario.AssertStopped("auto-review-retry stopped at review round 7/7");
    }

    [Xunit.Fact]
    public async Task E_Human_standard_reviewer_retry_grants_exactly_one_fresh_budget()
    {
        var scenario = AtConsecutiveCap();
        var intentId = await ApplyReviewerRetry(scenario, OperatorActorKind.Human, null);
        var applied = Assert.Single(scenario.Goal.Timeline.Where(evt => evt.OperatorIntentApplied?.IntentId == intentId));
        Assert.Equal(RetryRoundKind.Standard, applied.OperatorIntentApplied!.RetryRoundKind);
        for (var round = 1; round <= 7; round++)
        {
            scenario.Review([Same()]);
            if (round < 7)
            {
                scenario.AssertRetried(round);
                if (round == 1) Assert.Contains($"review_budget_reset=operator-retry intent={intentId}", scenario.Message);
            }
            else scenario.AssertStopped("stopped at review round 7/7");
        }
    }

    [Xunit.Fact]
    public async Task F_Mechanical_or_agent_reviewer_retry_does_not_reset_budget()
    {
        foreach (var (actor, kind) in new[]
        {
            (OperatorActorKind.Human, (RetryRoundKind?)RetryRoundKind.Mechanical),
            (OperatorActorKind.Agent, (RetryRoundKind?)null)
        })
        {
            var scenario = AtConsecutiveCap();
            var intentId = await ApplyReviewerRetry(scenario, actor, kind);
            var applied = Assert.Single(scenario.Goal.Timeline.Where(evt => evt.OperatorIntentApplied?.IntentId == intentId));
            Assert.Equal(kind ?? RetryRoundKind.Standard, applied.OperatorIntentApplied!.RetryRoundKind);
            scenario.Review([Same()]);
            scenario.AssertStopped("stopped at review round 7/7");
        }
    }

    [Xunit.Fact]
    public void G_Spec_defect_does_not_prevent_real_shrink()
    {
        var scenario = new Scenario();
        for (var round = 1; round <= 9; round++)
        {
            scenario.Review([.. ShrinkingRound(round), Spec()]);
            scenario.AssertRetried(1);
            if (round > 1) Assert.Contains("review_budget_reset=open-set-shrank", scenario.Message);
        }
    }

    [Xunit.Fact]
    public void H_Only_spec_defect_routes_to_operator_without_counting_a_retry()
    {
        var scenario = new Scenario();
        var before = ReviewRetryCapReceipt.Create(scenario.Goal, 7).Round;
        scenario.Review([Spec()]);
        Assert.Null(scenario.Message);
        Assert.DoesNotContain(scenario.Goal.Timeline, evt => evt.Kind == ProgressKind.TaskRetried);
        Assert.Equal(before, ReviewRetryCapReceipt.Create(scenario.Goal, 7).Round);
        Assert.Contains("operator-owned evidence", scenario.Escalation);
    }

    [Xunit.Fact]
    public void I_Closing_only_spec_defect_does_not_reset_budget()
    {
        var scenario = new Scenario();
        for (var round = 1; round <= 7; round++)
        {
            scenario.Review([Spec(round == 1 ? ReviewFindingState.Open : ReviewFindingState.Resolved), Same()]);
            if (round < 7) scenario.AssertRetried(round);
            else scenario.AssertStopped("stopped at review round 7/7");
        }
    }

    [Xunit.Fact]
    public async Task J_Shrink_does_not_reset_lifetime_but_human_retry_does()
    {
        var scenario = new Scenario();
        Assert.Equal(3, ConductorAutonomyPolicy.Permissive.ReviewAutoRetryLifetimeMultiplier);
        for (var round = 1; round <= 21; round++)
        {
            scenario.Review(ShrinkingRound(round));
            if (round < 21) scenario.AssertRetried(1);
            else
            {
                scenario.AssertStopped("auto-review-retry stopped at lifetime backstop: total review round 21/21; consecutive non-shrinking round 1/7;");
                AssertOperatorActions(scenario);
            }
        }
        await ApplyReviewerRetry(scenario, OperatorActorKind.Human, null);
        scenario.Review(ShrinkingRound(22));
        scenario.AssertRetried(1);
        Assert.Equal(2, ReviewRetryCapReceipt.Create(scenario.Goal, 7).LifetimeRound);
    }

    [Xunit.Fact]
    public void Tester_cap_advertises_existing_operator_verbs_and_reviewer_display_number()
    {
        var scenario = new Scenario();
        for (var round = 1; round <= 6; round++)
        {
            scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Developer.Id, $"auto-review-retry round {round}: prior verifying-role finding");
            PassVerification(scenario.Kernel, scenario.Goal, scenario.Developer, hasCommittedChanges: true);
        }
        FailTesterBlocker(scenario.Kernel, scenario.Goal, scenario.Tester, "Developer still misses the tester correctness blocker.");
        scenario.Advance();
        scenario.AssertStopped("auto-review-retry stopped at review round 7/7");
        AssertOperatorActions(scenario);
    }

    private static Scenario AtConsecutiveCap()
    {
        var scenario = new Scenario();
        for (var round = 1; round <= 7; round++)
        {
            scenario.Review([Same()]);
            if (round < 7) scenario.AssertRetried(round);
            else scenario.AssertStopped("stopped at review round 7/7");
        }
        return scenario;
    }

    private static void AssertOperatorActions(Scenario scenario)
    {
        var prefix = scenario.Goal.Id.Value[..8];
        var number = TaskDisplayNumber.Resolve(scenario.Goal, scenario.Reviewer.Id);
        var sentence = $"operator decision required: grant a fresh review budget by retrying Reviewer task {number} with retry --goal {prefix} {number} --text-file <path> --cause <cause>, " +
            $"waive the applicable criterion as an explicit override with goal-amend {prefix} --waive <exact-text> --reason <reason>, " +
            $"or supersede the requirement with supersede-goal {prefix} <reason>.";
        Assert.Contains(sentence, scenario.Escalation);
        Assert.DoesNotContain("continue", scenario.Escalation!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("split", scenario.Escalation!, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ApplyReviewerRetry(Scenario scenario, OperatorActorKind actor, RetryRoundKind? kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "review-budget-intent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            var intentId = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(intentId, intentId, OperatorIntentVerbs.Retry,
                scenario.Goal.Id.Value, scenario.Reviewer.Id.Value,
                JsonSerializer.Serialize(new RetryOperatorIntentPayload("Review the clarified contract.", kind,
                    RetryCause: RetryCause.ContractClarification), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [], "operator", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: actor);
            await store.EnqueueAsync(intent);
            new OperatorIntentCoordinator(store).ExecutePending(scenario.Kernel, scenario.Goal);
            Assert.Contains(scenario.Goal.Timeline, evt => evt.OperatorIntentApplied is { Outcome: "applied" } applied && applied.IntentId == intentId);
            Assert.Equal(WorkTaskStatus.Assigned, scenario.Reviewer.Status);
            return intentId;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ReviewFinding[] ShrinkingRound(int round) => round == 1 ? [ShrinkFinding(1)] :
        [ShrinkFinding(round - 1, ReviewFindingState.Resolved), ShrinkFinding(round)];

    private static ReviewFinding ShrinkFinding(int id, ReviewFindingState state = ReviewFindingState.Open) =>
        new($"F-{id}", state, new ReviewFindingLocation($"src/Shrink{id}.cs", $"Shrink{id}.Run"),
            $"Defect {id} remains.", FindingSeverity.Blocking, FindingCategory.Correctness);

    private static ReviewFinding Repeat(string id, ReviewFindingState state = ReviewFindingState.Open) =>
        new($"F-{id}", state, new ReviewFindingLocation($"src/Repeat{id}.cs", $"Repeat{id}.Run"),
            $"Repeat defect {id}.", FindingSeverity.Blocking, FindingCategory.Correctness);

    private static ReviewFinding Same() => new("F-SAME", ReviewFindingState.Open,
        new ReviewFindingLocation("src/Same.cs", "Same.Run"), "Same defect remains.", FindingSeverity.Blocking, FindingCategory.Correctness);

    private static ReviewFinding Spec(ReviewFindingState state = ReviewFindingState.Open) => new("S-SPEC", state,
        new ReviewFindingLocation("src/Spec.cs", "Spec.Contract"), "The brief requests a class that does not exist.", FindingSeverity.Blocking, FindingCategory.SpecDefect);

    private sealed class Scenario
    {
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public TaskSpec Reviewer { get; }
        public string? Message { get; private set; }
        public string? Escalation { get; private set; }
        private readonly ConductorDriver _driver;
        private int _reviewNumber;
        private int _retriesBeforeAdvance;

        public Scenario()
        {
            (Kernel, Goal) = SoftwareGoal();
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            PassVerification(Kernel, Goal, Developer, hasCommittedChanges: true);
            PassVerification(Kernel, Goal, Tester);
            _driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None,
                dispatchAndStart: _ => throw new InvalidOperationException("Finding recovery must not dispatch inline."),
                retryTask: Retry,
                retryTaskWithRoundKind: (gid, tid, message, kind) => Retry(gid, tid, message, kind),
                retryTaskWithCause: (gid, tid, message, kind, _) => Retry(gid, tid, message, kind),
                writeEscalation: (_, _, message) => Escalation = message);
        }

        private TaskSpec Retry(GoalId gid, TaskId tid, string message) => Retry(gid, tid, message, null);

        private TaskSpec Retry(GoalId gid, TaskId tid, string message, RetryRoundKind? kind)
        {
            Assert.Equal(Developer.Id, tid);
            Message = message;
            return Kernel.RetryTask(gid, tid, message, retryRoundKind: kind);
        }

        public void Review(IReadOnlyList<ReviewFinding> findings)
        {
            var commit = (++_reviewNumber).ToString("x40");
            FailReviewerNeedsWork(Kernel, Goal, Reviewer, "Blocking findings remain.", findings: findings,
                reviewedCommit: commit, touchedAnchors: findings.Where(finding => finding.State == ReviewFindingState.Resolved)
                    .Select(finding => finding.Location).ToArray());
            Assert.Null(Reviewer.LastVerification!.ReviewFindingContractViolation);
            Advance();
            if (Message is not null)
            {
                PassVerification(Kernel, Goal, Developer, hasCommittedChanges: true);
                PassVerification(Kernel, Goal, Tester);
            }
        }

        public void Advance()
        {
            Message = Escalation = null;
            _retriesBeforeAdvance = Goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried);
            _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        public void AssertRetried(int round)
        {
            Assert.Null(Escalation);
            Assert.StartsWith($"auto-review-retry round {round} convergence brief", Message);
            var retry = Assert.Single(Goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried).Skip(_retriesBeforeAdvance)
                .Where(evt => evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(Developer.Id, retry.TaskId);
            Assert.Equal(Message, retry.Message);
        }

        public void AssertStopped(string prefix)
        {
            Assert.Null(Message);
            Assert.Contains(prefix, Escalation);
            Assert.Equal(_retriesBeforeAdvance, Goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried));
        }
    }
}
