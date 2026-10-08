using Mcg.AgentOrchestrator.Core;

// Parallel-safe: snapshot-only inputs and fixed recorded timestamps.
public sealed class RoundValueCheckSliceTests
{
    private const string GoalId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Structural = "developer-completion structural pre-check failed: ";
    private const string Acceptance = "Acceptance criteria unmet; retrying task with feedback (attempt 1/3): ";
    private static readonly DateTimeOffset First = RoundValueFixture.At("2026-09-24T01:00:00Z");
    private static readonly DateTimeOffset Second = First.AddHours(1);

    [Fact]
    public void StructuralMessagesWinOverFailedVerdictsInRecordedLineOrder()
    {
        var samples = new[]
        {
            ("src/A.cs has 700 lines, exceeding the recorded ceiling of 650. Split it.\r\nclass:B has 700, exceeding the type ceiling of 650.", "source-size-ratchet:src/A.cs"),
            ("class:B has 700, exceeding the type ceiling of 650. Split it.\r\nsrc/A.cs has 700 lines, exceeding the recorded ceiling of 650.", "source-class-ratchet:B"),
            ("config/acceptance-manifest.json: missing lane\r\nsecond diagnostic", "acceptance-manifest")
        };
        foreach (var (message, key) in samples)
        {
            var slice = Build(Create(Structural + message, verifications: [Verdict(1)]));
            AssertCounts(slice.Rows.Single(r => r.Check == key), 1, 1, 0, 0, 1, 0);
            Assert.Equal(0, slice.Rows.Single(r => r.Check == "unattributed").Rounds);
            Assert.Equal(1, slice.Rows.Sum(r => r.Rounds));
        }
    }

    [Fact]
    public void FailedPriorVerdictWinsButSuccessfulAndUnpairedVerdictsDoNot()
    {
        var slice = Build(Create(Acceptance + "Cli: 2 failed", verifications: [Verdict(1)]));
        Assert.Equal("worker-build-check-failed", slice.Rows[0].Check);
        var success = Build(Create(Acceptance + "Cli: 2 failed", verifications: [Verdict(0)]));
        Assert.Equal("acceptance:Cli", success.Rows[0].Check);
        var currentOnly = Build(Create(Acceptance + "Cli: 2 failed",
            verifications: [Verdict(1) with { CompletedAt = Second.AddMinutes(5) }]));
        Assert.Equal("acceptance:Cli", currentOnly.Rows[0].Check);
        var malformedStructural = Build(Create(Structural + "unknown diagnostic", verifications: [Verdict(1)]));
        Assert.Equal("worker-build-check-failed", malformedStructural.Rows[0].Check);
    }

    [Fact]
    public void LinkedReviewerFindingsWinOverAcceptanceInReceiptOrder()
    {
        var findings = new[]
        {
            Finding("f-1", FindingCategory.CodeQuality), Finding("f-2", FindingCategory.Correctness)
        };
        var goal = Create(Acceptance + "Cli: 2 failed", receipts: [Receipt("missing", "f-2", "f-1")],
            reviewVerifications: [Verdict(0) with { MergedReviewFindings = findings }]);
        Assert.Equal("reviewer:correctness", Build(goal).Rows[0].Check);
        var reversed = goal with { Tasks = [goal.Tasks[0] with { RetryAdmissionHistory = [Receipt("f-1", "f-2")] }, goal.Tasks[1]] };
        Assert.Equal("reviewer:code-quality", Build(reversed).Rows[0].Check);
        var failed = goal with { Tasks = [goal.Tasks[0] with { VerificationHistory = [Verdict(1)] }, goal.Tasks[1]] };
        Assert.Equal("worker-build-check-failed", Build(failed).Rows[0].Check);
        var future = Create(Acceptance + "Cli: 2 failed", receipts: [Receipt("f-2")],
            reviewVerifications: [Verdict(0) with { CompletedAt = Second.AddMinutes(5), MergedReviewFindings = findings }]);
        Assert.Equal("acceptance:Cli", Build(future).Rows[0].Check);
        var unlinked = Create(Acceptance + "Cli: 2 failed",
            receipts: [Receipt("f-2") with { LinkedDispatchAt = First }],
            reviewVerifications: [Verdict(0) with { MergedReviewFindings = findings }]);
        Assert.Equal("acceptance:Cli", Build(unlinked).Rows[0].Check);
    }

    [Fact]
    public void AcceptanceMessagesUseFirstSummaryCheckAndPreserveItsName()
    {
        Assert.Equal("acceptance:Cli", Build(Create(Acceptance + "Cli: 2 failed\r\nCore: 1 failed")).Rows[0].Check);
        var recorded = Acceptance + "Concrete acceptance failure evidence:\r\nfailed test: Example\r\n" +
            "Acceptance criteria summary:\r\nCLI Check: 2 failed\r\nCore: 1 failed";
        Assert.Equal("acceptance:CLI Check", Build(Create(recorded)).Rows[0].Check);
        Assert.Equal("family:GateRed", Build(Create(Acceptance)).Rows[0].Check);
    }

    [Fact]
    public void FamilyFallbackPartitionsReworkRoundsAndCountsDistinctGoals()
    {
        var goal = Create("Invalidated Developer task because upstream changed");
        var task = goal.Tasks[0] with
        {
            DispatchHistory = [.. goal.Tasks[0].DispatchHistory!, RoundValueFixture.Dispatch("2026-09-24T03:00:00Z")]
        };
        goal = goal with { Tasks = [task], Timeline = [.. goal.Timeline,
            new(GoalId, "dev", ProgressKind.TaskRetried, "Invalidated Developer task because upstream changed", Second.AddMinutes(50)),
            new(GoalId, "dev", ProgressKind.TaskCompleted, "done", Second.AddHours(1).AddMinutes(10))] };
        var slice = Build(goal);
        AssertCounts(slice.Rows[0], 2, 0, 2, 0, 1, 0);
        Assert.Equal("family:DownstreamRerun", slice.Rows[0].Check);
        Assert.Equal(2, slice.Rows.Sum(r => r.Rounds));
        Assert.Equal(2, slice.Rows.Sum(r => r.Productive + r.Overhead + r.Wasted));
        Assert.Empty(slice.Rows[0].OutcomeClasses);
    }

    [Fact]
    public void UnattributedRowIsAlwaysPresentAndDoesNotIncludeFirstRounds()
    {
        var row = Assert.Single(Build(Create(null)).Rows);
        Assert.Equal("unattributed", row.Check);
        AssertCounts(row, 1, 0, 0, 1, 1, 0);
        var empty = Assert.Single(RoundValueCheckSlice.Build([], RoundValueFixture.Since, RoundValueFixture.Until).Rows);
        AssertCounts(empty, 0, 0, 0, 0, 0, 0);
        Assert.Equal("unattributed", empty.Check);
        var firstOnly = Create(null);
        firstOnly = firstOnly with { Tasks = [firstOnly.Tasks[0] with { DispatchHistory = [firstOnly.Tasks[0].DispatchHistory![0]] }] };
        Assert.Equal(0, Assert.Single(Build(firstOnly).Rows).Rounds);
    }

    [Fact]
    public void LostGoalKeepsCheckKeyWhileItsValueBecomesWasted()
    {
        var goal = Create(Structural + "src/A.cs has 700 lines, exceeding the recorded ceiling of 650.");
        var landed = Build(goal).Rows[0];
        var lost = Build(goal with { Status = GoalStatus.Cancelled }).Rows[0];
        Assert.Equal("source-size-ratchet:src/A.cs", landed.Check);
        Assert.Equal(landed.Check, lost.Check);
        AssertCounts(landed, 1, 1, 0, 0, 1, 0);
        AssertCounts(lost, 1, 0, 0, 1, 0, 1);
    }

    [Fact]
    public void OutcomeClassesComeOnlyFromNotesWithinTheAttributedRound()
    {
        var goal = Create("Invalidated Developer task because upstream changed");
        goal = goal with { Timeline = [.. goal.Timeline,
            new(GoalId, "dev", ProgressKind.TaskNote, "CLASSIFIER rule=x; outcome_class=worker-failure", First.AddMinutes(5)),
            new(GoalId, "dev", ProgressKind.TaskNote, "CLASSIFIER rule=x; outcome_class=success", Second.AddMinutes(5)),
            new(GoalId, "dev", ProgressKind.OperatorTaskNote, "CLASSIFIER rule=x; outcome_class=reconciled-to-success", Second.AddMinutes(6))] };
        var row = Build(goal).Rows[0];
        Assert.Equal(new RoundValueCheckOutcomeCount("reconciled-to-success", 1), Assert.Single(row.OutcomeClasses));
    }

    [Fact]
    public void CohortUsesTerminalGoalsLastDispatchInHalfOpenWindow()
    {
        var goal = Create("Invalidated Developer task because upstream changed");
        Assert.Equal(0, Assert.Single(Build(goal with { Status = GoalStatus.Active }).Rows).Rounds);
        Assert.Equal(0, Assert.Single(RoundValueCheckSlice.Build(ToGoals(goal), First, Second).Rows).Rounds);
        Assert.Equal(1, RoundValueCheckSlice.Build(ToGoals(goal), Second, Second.AddDays(1)).Rows.Sum(r => r.Rounds));
        Assert.Throws<ArgumentException>(() => RoundValueCheckSlice.Build(ToGoals(goal), Second, First));
    }

    private static RoundValueCheckSlice Build(GoalSnapshot goal) =>
        RoundValueCheckSlice.Build(ToGoals(goal), RoundValueFixture.Since, RoundValueFixture.Until);

    private static IReadOnlyList<Goal> ToGoals(GoalSnapshot goal) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([goal], [])).Goals.ToArray();

    private static GoalSnapshot Create(string? retry, TaskVerificationSnapshot[]? verifications = null,
        RetryAdmissionReceipt[]? receipts = null, TaskVerificationSnapshot[]? reviewVerifications = null)
    {
        var task = RoundValueFixture.Task("dev", AgentRole.Developer, WorkTaskStatus.Completed,
            RoundValueFixture.Dispatch("2026-09-24T01:00:00Z"), RoundValueFixture.Dispatch("2026-09-24T02:00:00Z"))
            with { VerificationHistory = verifications ?? [], RetryAdmissionHistory = receipts ?? [] };
        var tasks = new List<TaskSnapshot> { task };
        if (reviewVerifications is not null)
            tasks.Add(RoundValueFixture.Task("review", AgentRole.Reviewer, WorkTaskStatus.Completed,
                RoundValueFixture.Dispatch("2026-09-24T01:00:00Z")) with { VerificationHistory = reviewVerifications });
        var events = new List<ProgressEventSnapshot>
        {
            new(GoalId, "dev", ProgressKind.TaskCompleted, "done", First.AddMinutes(10)),
            new(GoalId, "dev", ProgressKind.TaskCompleted, "done", Second.AddMinutes(10))
        };
        if (retry is not null) events.Add(new(GoalId, "dev", ProgressKind.TaskRetried, retry, First.AddMinutes(50)));
        return new(GoalId, "Checks", GoalStatus.Completed, tasks, events);
    }

    private static TaskVerificationSnapshot Verdict(int exit) =>
        new("command", "root", exit, "", "", First.AddMinutes(20), CompletionVerdictRule: "worker-build-check-failed");

    private static ReviewFinding Finding(string id, FindingCategory category) =>
        new(id, ReviewFindingState.Open, new("src/A.cs", "Method"), "recorded finding", Category: category);

    private static RetryAdmissionReceipt Receipt(params string[] ids) => new("receipt", RetryCause.NewSourceFinding,
        new(1, "fingerprint"), RetryAdmissionDecision.Allowed, RetryAdmissionRoute.SameRole,
        PaidRouteClassification.Unknown, Second, First.AddMinutes(50), StableFindingIds: ids);

    private static void AssertCounts(RoundValueCheckRow row, int rounds, int productive, int overhead,
        int wasted, int landed, int lost) => Assert.Equal((rounds, productive, overhead, wasted, landed, lost),
            (row.Rounds, row.Productive, row.Overhead, row.Wasted, row.GoalsLanded, row.GoalsLost));
}
