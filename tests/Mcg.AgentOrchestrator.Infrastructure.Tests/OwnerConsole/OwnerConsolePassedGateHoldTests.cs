using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: instance-owned kernels, injected candidate evidence and a fixed clock; no I/O or GUI driver.
public sealed class OwnerConsolePassedGateHoldTests
{
    private const string Candidate = "62f41de062f41de062f41de062f41de062f41de0";
    private const string OlderCandidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddHours(2);

    [Theory]
    [InlineData("untyped/AcceptanceQueue", "\n", "gate-passed")]
    [InlineData("landing/landing-held-after-pass", "\r\n", "passed")]
    public async Task Board_CurrentCandidatePassed_ShowsFirstLineAndRetainsFullDetail(string kind, string newline, string outcome)
    {
        const string firstLine = "acceptance passed at 62f41de0; landing held";
        const string secondLine = "gate test obligation: selector-not-found";
        var harness = Fixture(kind, firstLine + newline + secondLine);
        var goal = Assert.Single(harness.Kernel.Goals);
        var calls = 0;
        var model = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), harness.Clock, candidateGate: observed =>
            {
                Assert.Same(goal, observed);
                calls++;
                return (Candidate, new[] { Attempt(goal, Candidate, outcome) });
            }).BuildBoardAsync();

        Assert.Equal(1, calls);
        var stage = Assert.Single(model.Board).Stage;
        Assert.StartsWith("gate passed, landing held:", stage);
        Assert.Contains(firstLine, stage);
        Assert.EndsWith(", 1h30m", stage);
        Assert.DoesNotContain(secondLine, stage);
        Assert.DoesNotContain("in queue", stage);
        Assert.DoesNotContain("waiting for gate", stage);
        var detail = OwnerConsoleLandingBlockerText.Detail(goal, TimeZoneInfo.Utc);
        Assert.Contains(firstLine, detail!);
        Assert.Contains(secondLine, detail!);
        Assert.Equal(firstLine + newline + secondLine, goal.LandingBlocker!.Detail);
    }

    [Theory]
    [InlineData(false, "waiting for gate (1st in queue)")]
    [InlineData(true, "waiting for gate (1st in queue), 1h30m")]
    public void Board_NoAttempt_KeepsQueueText(bool blocker, string expected)
    {
        var goal = Assert.Single(Fixture(blocker ? "untyped/AcceptanceQueue" : null).Kernel.Goals);
        Assert.Equal(expected, Describe(goal, Candidate, []));
    }

    [Theory]
    [InlineData("untyped/AcceptanceQueue")]
    [InlineData("landing/landing-held-after-pass")]
    public void Board_OldOrUnknownCandidateOrSupersedingFailure_DoesNotClaimPass(string kind)
    {
        var goal = Assert.Single(Fixture(kind).Kernel.Goals);
        var pass = Attempt(goal, OlderCandidate, "gate-passed");
        Assert.DoesNotContain("passed", Describe(goal, Candidate, [pass]));
        Assert.DoesNotContain("passed", Describe(goal, null, [pass]));
        Assert.DoesNotContain("passed", Describe(goal, " ", [pass]));
        Assert.DoesNotContain("passed", Describe(goal, Candidate, [pass with { BranchHeadSha = null }]));
        Assert.DoesNotContain("passed", Describe(goal, Candidate, [pass with { BranchHeadSha = Candidate.ToUpperInvariant() }]));
        var currentPass = pass with { BranchHeadSha = Candidate };
        Assert.DoesNotContain("passed", Describe(goal, Candidate,
            [currentPass, currentPass with { At = Now.AddMinutes(1), AcceptanceOutcome = "failed" }]));
        Assert.DoesNotContain("passed", Describe(goal, Candidate,
            [currentPass, currentPass with { AcceptanceOutcome = "failed" }]));
    }

    [Fact]
    public void Board_UnreadableEvidence_KeepsExistingPhrase()
    {
        var goal = Assert.Single(Fixture("untyped/AcceptanceQueue").Kernel.Goals);
        Assert.Equal("waiting for gate (1st in queue), 1h30m",
            OwnerConsoleStageDescriber.Describe(goal, OwnerConsoleBoardSortKey.For(goal), 1, Now,
                candidateGate: _ => throw new IOException("unreadable journal")));
    }

    [Theory]
    [InlineData(GoalStatus.Verifying, true)]
    [InlineData(GoalStatus.Verified, false)]
    public void Board_IneligibleGoal_DoesNotReadCandidate(GoalStatus status, bool blocker)
    {
        var goal = Assert.Single(Fixture(blocker ? "landing/landing-held-after-pass" : null, status: status).Kernel.Goals);
        var calls = 0;
        var text = OwnerConsoleStageDescriber.Describe(goal, OwnerConsoleBoardSortKey.For(goal), 1, Now,
            candidateGate: _ => { calls++; return (Candidate, new[] { Attempt(goal, Candidate, "passed") }); });
        Assert.Equal(0, calls);
        Assert.DoesNotContain("passed", text);
    }

    private static string Describe(Goal goal, string? candidate, IReadOnlyList<GoalOperationJournalEntry> attempts) =>
        OwnerConsoleStageDescriber.Describe(goal, OwnerConsoleBoardSortKey.For(goal), 1, Now,
            candidateGate: _ => (candidate, attempts));

    private static GoalOperationJournalEntry Attempt(Goal goal, string? candidate, string outcome) =>
        new("attempt", goal.Id, "acceptance", GoalOperationStatus.Completed, Now, "gate result",
            BranchHeadSha: candidate, AcceptanceOutcome: outcome);

    private static OwnerConsoleHarness Fixture(string? kind, string reason = "hold reason", GoalStatus status = GoalStatus.Verified)
    {
        var harness = new OwnerConsoleHarness(new OwnerConsoleTestClock { Now = Now });
        harness.AddGoal("66767abd000000000000000000000000", "held landing", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = status,
                Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
                LandingBlocker = kind is null ? null : new(kind, reason, "hold", Now.AddMinutes(-90), "unmapped", "unmapped")
            }).ToArray()
        });
        return harness;
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }
}
