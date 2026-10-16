using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: receipt store, heads, projection and landing are in-memory seams.
public sealed class ConductorBatchLoopTestsLandingHeldReceiptRelease : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLandingHeldReceiptRelease(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void Apply_OperatorWait_ReleasesOnceAndLandsAfterAnswer()
    {
        var fixture = new MemoryFixture();
        var wait = fixture.RequestOperatorWait();

        var first = fixture.Tick(1);

        Assert.Equal(fixture.ReleaseLine(wait), Assert.Single(Lines(first.Progress, "TRAIN_RECEIPT_RELEASED")));
        Assert.Empty(Lines(first.Progress, "TRAIN_RECEIPT_HELD"));
        Assert.DoesNotContain(first.Progress, line => line.Contains("reason=PassedTrainReceiptHeld", StringComparison.Ordinal));
        Assert.Equal(fixture.Goals.Take(2).Select(goal => goal.Id), first.Candidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready)
            .Select(candidate => candidate.GoalId));
        Assert.Empty(first.Results);
        Assert.Empty(fixture.LandingRequests);

        var second = fixture.Tick(2);

        Assert.Empty(Lines(second.Progress, "TRAIN_RECEIPT_RELEASED"));
        Assert.Empty(Lines(second.Progress, "TRAIN_RECEIPT_HELD"));
        Assert.Empty(fixture.LandingRequests);
        fixture.Kernel.SubmitHumanInput(wait.Id, "Evidence supplied", supersedeSiblings: false);

        var third = fixture.Tick(3);

        Assert.Equal(fixture.Goals.Select(goal => goal.Id),
            Assert.Single(fixture.LandingRequests).Members.Select(member => member.GoalId));
        Assert.Equal(3, third.Results.Count);
        Assert.Empty(Lines(third.Progress, "TRAIN_RECEIPT_RELEASED"));
    }

    [Fact]
    public void Apply_OwnerReviewHold_HoldsAllMembersAtTickOne()
    {
        var fixture = new MemoryFixture();
        fixture.OwnerReviewHolds.Add(fixture.Goals[2].Id);

        var tick = fixture.Tick(1);

        var held = Assert.Single(Lines(tick.Progress, "TRAIN_RECEIPT_HELD"));
        Assert.Contains($"member={fixture.Goals[2].Id.Value[..8]} reason=OwnerReviewHold heldTicks=1", held);
        Assert.Equal(3, Lines(tick.Progress, "ADMISSION").Count(line =>
            line.Contains("reason=PassedTrainReceiptHeld", StringComparison.Ordinal)));
        Assert.Equal(fixture.Goals.Select(goal => goal.Id.Value).Order(), tick.Results.Keys.Order());
        Assert.Empty(tick.Candidates);
        Assert.Empty(Lines(tick.Progress, "TRAIN_RECEIPT_RELEASED"));
        Assert.Empty(fixture.LandingRequests);
    }

    [Fact]
    public void Apply_OwnerReviewAndLandingHold_ReleasesForLandingHold()
    {
        var fixture = new MemoryFixture();
        fixture.OwnerReviewHolds.Add(fixture.Goals[1].Id);
        var wait = fixture.RequestOperatorWait();

        var tick = fixture.Tick(1);

        Assert.Equal(fixture.ReleaseLine(wait), Assert.Single(Lines(tick.Progress, "TRAIN_RECEIPT_RELEASED")));
        Assert.Empty(Lines(tick.Progress, "TRAIN_RECEIPT_HELD"));
        Assert.Empty(tick.Results);
        Assert.Empty(fixture.LandingRequests);
    }

    [Fact]
    public void Apply_HeldMemberAbsentFromCandidates_ProjectsWaitAndReleases()
    {
        var fixture = new MemoryFixture();
        var wait = fixture.RequestOperatorWait();

        var tick = fixture.Tick(1, omitThirdCandidate: true);

        Assert.Equal(fixture.ReleaseLine(wait), Assert.Single(Lines(tick.Progress, "TRAIN_RECEIPT_RELEASED")));
        Assert.Equal(2, tick.Candidates.Length);
        Assert.All(tick.Candidates, candidate => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidate.ProjectionResult));
        Assert.Empty(fixture.LandingRequests);
    }

    private static string[] Lines(IEnumerable<string> progress, string tag) => progress
        .Where(line => line.StartsWith(tag + " ", StringComparison.Ordinal)).ToArray();

    private sealed record TickResult(ConductorSpeculativeAcceptanceCandidate[] Candidates,
        Dictionary<string, ParallelLandingOutcome> Results, List<string> Progress);

    private sealed class MemoryFixture
    {
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal[] Goals { get; }
        internal MergeTrainReceipt Receipt { get; }
        internal HashSet<GoalId> OwnerReviewHolds { get; } = [];
        internal List<ConductorMergeTrainSelection> LandingRequests { get; } = [];
        private readonly PassedMergeTrainReceiptHolds _holds = new();
        private readonly HashSet<string> _reportedStale = [];
        private readonly string _mainRevision = new('a', 40);
        private readonly string _candidateRevision = new('b', 40);

        internal MemoryFixture()
        {
            Goals = Enumerable.Range(0, 3).Select(index =>
                CreateVerifiedSimpleGoal(Kernel, $"Receipt release member {index}")).ToArray();
            var selection = new ConductorMergeTrainSelection(Goals.Select(goal =>
                ((GateReadyCandidateProjectionResult.Ready)Project(goal)).Projection).ToArray());
            var members = selection.BindMembers().Select(member => member.WithRebasedHead(new string('c', 40))).ToArray();
            Receipt = new MergeTrainReceipt("landing-held-passed-receipt",
                MergeTrainIdentity.Create(members, _mainRevision, new string('c', 40), "memory-manifest"),
                MergeTrainGateOutcome.Passed, DateTimeOffset.UnixEpoch, 0, [], 0, []);
        }

        internal HumanInputRequest RequestOperatorWait() => Kernel.RequestHumanInputDeduplicated(
            Goals[2].Id, null, "Supply operator evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: "operator", testNames: ["GateTests"]).Request;

        internal string ReleaseLine(HumanInputRequest wait) =>
            $"TRAIN_RECEIPT_RELEASED train={Receipt.Identity.Value} receipt={Receipt.ReceiptId} " +
            $"reason=landing-held member={Goals[2].Id.Value[..8]} request={wait.Id.Value[..8]}";

        private GateReadyCandidateProjectionResult Project(Goal goal)
        {
            GateReadyCandidateProjectionResult projection = OwnerReviewHolds.Contains(goal.Id)
                ? new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.OwnerReviewHold)
                : new GateReadyCandidateProjectionResult.Ready(new(
                    goal.Id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied,
                    ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
                    [$"src/{goal.Id.Value}.cs"], [$"resource:{goal.Id.Value}"],
                    new GateReadyMergeEvidence(_candidateRevision, _mainRevision,
                        GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)));
            return GroupedAdmissionLandingHold.Apply(projection, Kernel.GetPendingHumanInput(goal.Id));
        }

        internal TickResult Tick(int tick, bool omitThirdCandidate = false)
        {
            var results = new Dictionary<string, ParallelLandingOutcome>();
            var progress = new List<string>();
            var candidates = Goals.Where(goal => !omitThirdCandidate || goal.Id != Goals[2].Id)
                .Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id, Project(goal))).ToArray();
            var operations = new PassedMergeTrainReceiptAdmission.Operations(
                (ready, ineligible, stale, blocked, scopedGoals, observed) =>
                    PassedMergeTrainReceiptSelector.Find(ready, ineligible, stale, blocked, scopedGoals, observed,
                        _ => (_candidateRevision, _mainRevision), Goals, _ => [Receipt],
                        () => new HashSet<string>(),
                        (selection, receipt) => selection.Members.Select(member => member.GoalId)
                            .SequenceEqual(receipt.Identity.Members.Select(member => member.GoalId)), TrunkBranchName.Default),
                (goal, _) => Project(goal),
                (selection, _, _) =>
                {
                    LandingRequests.Add(selection);
                    return new ConductorMergeTrainRunResult(Receipt,
                        selection.Members.ToDictionary(member => member.GoalId.Value, member =>
                            Result(member.GoalId, new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp))),
                        [], $"outcome=passed attempts=1 landings=3 receipt={Receipt.ReceiptId}");
                });
            var admission = PassedMergeTrainReceiptAdmission.Apply(operations, ConductorAutonomyPolicy.Permissive,
                Goals, candidates, Goals, results, tick, [], _holds, _reportedStale, progress.Add,
                (line, _) => progress.Add(line),
                (goal, reason) => Result(goal.Id, new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason)));
            return new(admission.Candidates, results, progress);
        }

        private static ConductorAdvanceResult Result(GoalId id, ConductorAdvanceOutcome outcome) =>
            new(id.Value, id.Value[..8], "Permissive", outcome);
    }
}
