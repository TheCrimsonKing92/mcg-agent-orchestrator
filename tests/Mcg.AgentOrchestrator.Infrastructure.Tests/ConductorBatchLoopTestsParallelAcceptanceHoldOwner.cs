using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each test owns its kernel and candidates; observation times are fixed.
public sealed class ConductorBatchLoopTestsParallelAcceptanceHoldOwner(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void ResourceConflict_FirstOverlappingGate_AssignsAcceptanceQueueAndNamesBlocker()
    {
        var (_, goal) = SimpleGoal();
        var (_, independentGoal) = SimpleGoal();
        var (_, blockingGoal) = SimpleGoal();
        var (_, laterBlockingGoal) = SimpleGoal();
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 2, ["src/Shared/Same.cs"]);
        var independent = ConductorParallelAcceptanceCandidate.Create(
            independentGoal, 0, ["src/Independent/Other.cs"]);
        var blocking = ConductorParallelAcceptanceCandidate.Create(blockingGoal, 1, ["src/Shared/Same.cs"]);
        var laterBlocking = ConductorParallelAcceptanceCandidate.Create(
            laterBlockingGoal, 3, ["src/Shared/Same.cs"]);
        Assert.False(independent.Overlaps(candidate));
        Assert.True(blocking.Overlaps(candidate));
        Assert.True(laterBlocking.Overlaps(candidate));

        var admission = Assert.IsType<ConductorBatchLoop.SoloAcceptanceAdmission>(
            ConductorBatchLoop.DetectSoloAcceptanceResourceConflict(
                candidate, [independent, blocking, laterBlocking]));
        Assert.Equal(ConductorBatchLoop.SoloAcceptanceAdmissionKind.ResourceConflict, admission.Kind);
        Assert.Same(candidate, admission.Candidate);
        var lines = new List<string>();
        var held = ApplyHold(admission, goal, MakeDriver(), lines);

        Assert.Equal(ConductorHoldOwner.AcceptanceQueue, held.Owner);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.StartsWith("parallel acceptance resource conflict", held.Reason, StringComparison.Ordinal);
        Assert.Contains($"goal:{blocking.GoalPrefix}", held.Reason, StringComparison.Ordinal);
        Assert.EndsWith("; retry on next conduct tick", held.Reason, StringComparison.Ordinal);
        Assert.Equal(
            $"parallel acceptance resource conflict with live acceptance goal:{blocking.GoalPrefix}; retry on next conduct tick",
            held.Reason);
        Assert.Empty(lines);
    }

    [Xunit.Fact]
    public void ResourceConflict_NoOverlappingGate_DoesNotDenyAdmission()
    {
        var (_, goal) = SimpleGoal();
        var (_, independentGoal) = SimpleGoal();
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 1, ["src/Shared/Same.cs"]);
        var independent = ConductorParallelAcceptanceCandidate.Create(
            independentGoal, 0, ["src/Independent/Other.cs"]);
        Assert.False(independent.Overlaps(candidate));

        Assert.Null(ConductorBatchLoop.DetectSoloAcceptanceResourceConflict(candidate, [independent]));
        Assert.Null(ConductorBatchLoop.DetectSoloAcceptanceResourceConflict(candidate, []));
    }

    [Xunit.Fact]
    public void Fairness_OlderVerifiedGoal_AssignsAcceptanceQueueAndPreservesReason()
    {
        var (_, goal) = SimpleGoal();
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Shared/Same.cs"]);
        const string reason = "parallel acceptance fairness waiting for oldest verified goal abcd1234; retry on next conduct tick";
        const string progress = "ADMISSION tick=1 result=deferred reason=parallel-acceptance-fairness";
        var admission = new ConductorBatchLoop.SoloAcceptanceAdmission(
            ConductorBatchLoop.SoloAcceptanceAdmissionKind.Fairness, candidate, reason, progress);
        var lines = new List<string>();

        var held = ApplyHold(admission, goal, MakeDriver(), lines);

        Assert.Equal(ConductorHoldOwner.AcceptanceQueue, held.Owner);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(reason, held.Reason);
        Assert.Equal(progress, Assert.Single(lines));
    }

    [Xunit.Fact]
    public void ResourceConflict_TrackedOwnerlessHold_ClearsWithoutStall()
    {
        var (kernel, goal) = SimpleGoal();
        var (_, blockingGoal) = SimpleGoal();
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 1, ["src/Shared/Same.cs"]);
        var blocking = ConductorParallelAcceptanceCandidate.Create(blockingGoal, 0, ["src/Shared/Same.cs"]);
        var admission = Assert.IsType<ConductorBatchLoop.SoloAcceptanceAdmission>(
            ConductorBatchLoop.DetectSoloAcceptanceResourceConflict(candidate, [blocking]));
        var driver = MakeDriver();
        var lines = new List<string>();
        var held = ApplyHold(admission, goal, driver, lines);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.StartsWith("parallel acceptance resource conflict", held.Reason, StringComparison.Ordinal);
        var firstObservation = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var threshold = TimeSpan.FromMinutes(10);
        var changed = new HashSet<GoalId>();
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal,
            held with { Owner = ConductorHoldOwner.None }, firstObservation, threshold, changed, lines);
        Assert.NotNull(goal.CurrentHold);
        Assert.Empty(lines);
        changed.Clear();

        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
            firstObservation.AddMinutes(11), threshold, changed, lines);

        Assert.Null(goal.CurrentHold);
        Assert.Equal(ConductorHoldOwner.AcceptanceQueue, held.Owner);
        Assert.Contains(goal.Id, changed);
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
            firstObservation.AddMinutes(22), threshold, changed, lines);
        Assert.Null(goal.CurrentHold);
        Assert.DoesNotContain(lines, line => line.Contains("GOAL_STALLED", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("ownerless-hold-stalled", StringComparison.Ordinal));
        Assert.Empty(lines);
    }

    private static ConductorAdvanceOutcome.Held ApplyHold(
        ConductorBatchLoop.SoloAcceptanceAdmission admission, Goal goal,
        ConductorDriver driver, List<string> lines)
    {
        var outcome = Assert.IsType<ParallelLandingOutcome>(ConductorBatchLoop.ApplySoloAcceptanceHold(
            admission, driver, goal, ConductorAutonomyPolicy.Conservative, lines));
        return Assert.IsType<ConductorAdvanceOutcome.Held>(outcome.Result.Outcome);
    }
}
