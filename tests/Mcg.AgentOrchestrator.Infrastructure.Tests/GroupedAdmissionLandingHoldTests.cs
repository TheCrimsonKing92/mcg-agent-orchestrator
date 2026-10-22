using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel and uses only in-memory projections.
public sealed class GroupedAdmissionLandingHoldTests
{
    [Theory]
    [InlineData("operator", true)]
    [InlineData("owner", true)]
    [InlineData(null, true)]
    [InlineData("Acceptance", false)]
    [InlineData("other", true)]
    [InlineData("acceptance-gate", false)]
    public void Apply_UnsatisfiableWait_ExcludesWithRequestId(string? owner, bool namesTests)
    {
        var (kernel, goal) = Setup();
        var wait = Request(kernel, goal, owner, namesTests);

        var result = GroupedAdmissionLandingHold.Apply(Ready(goal.Id), kernel.GetPendingHumanInput(goal.Id));

        var excluded = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result);
        Assert.Equal(GateReadyCandidateExclusionReason.LandingHeld, excluded.Reason);
        Assert.Equal(wait.Id.Value, excluded.HoldRequestId);
        Assert.False(GroupedAdmissionLandingHold.IsGateSatisfiable(wait));
    }

    [Fact]
    public void Apply_MixedWaits_ReportsFirstBlockingRequestInGivenOrder()
    {
        var (kernel, goal) = Setup();
        var gate = Request(kernel, goal, "acceptance-gate");
        var first = Request(kernel, goal, "operator");
        var second = Request(kernel, goal, "owner");

        var excluded = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
            GroupedAdmissionLandingHold.Apply(Ready(goal.Id), [gate, first, second]));

        Assert.Equal(first.Id.Value, excluded.HoldRequestId);
        Assert.Same(second, GroupedAdmissionLandingHold.FindBlockingWait([gate, second, first]));
    }

    [Fact]
    public void Apply_NoWait_ReturnsSameReadyProjection()
    {
        var (kernel, goal) = Setup();
        var ready = Ready(goal.Id);
        Assert.Same(ready, GroupedAdmissionLandingHold.Apply(ready, kernel.GetPendingHumanInput(goal.Id)));
    }

    [Theory]
    [InlineData("acceptance-gate")]
    [InlineData("ACCEPTANCE-GATE")]
    [InlineData("Acceptance")]
    public void Apply_GateSatisfiableWait_ReturnsSameReadyProjection(string owner)
    {
        var (kernel, goal) = Setup();
        var wait = Request(kernel, goal, owner);
        var ready = Ready(goal.Id);

        Assert.True(GroupedAdmissionLandingHold.IsGateSatisfiable(wait));
        Assert.Same(ready, GroupedAdmissionLandingHold.Apply(ready, kernel.GetPendingHumanInput(goal.Id)));
    }

    [Fact]
    public void Apply_AnsweredWait_ReturnsSameReadyProjection()
    {
        var (kernel, goal) = Setup();
        var wait = Request(kernel, goal, "operator");
        kernel.SubmitHumanInput(wait.Id, "Evidence supplied", supersedeSiblings: false);
        var ready = Ready(goal.Id);

        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Assert.Same(ready, GroupedAdmissionLandingHold.Apply(ready, kernel.GetPendingHumanInput(goal.Id)));
        Assert.Null(GroupedAdmissionLandingHold.FindBlockingWait([wait]));
    }

    [Fact]
    public void Apply_ExistingConflict_PreservesExclusionAndPaths()
    {
        var (kernel, goal) = Setup();
        Request(kernel, goal, "operator");
        var excluded = new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.MergeConflict)
            { ConflictPaths = ["src/Conflict.cs"] };

        Assert.Same(excluded, GroupedAdmissionLandingHold.Apply(excluded, kernel.GetPendingHumanInput(goal.Id)));
        Assert.Equal(["src/Conflict.cs"], excluded.ConflictPaths);
    }

    [Fact]
    public void IsGateSatisfiable_OtherKindWithGateOwnerAndTests_ReturnsFalse()
    {
        var (kernel, goal) = Setup();
        var wait = kernel.RequestHumanInputDeduplicated(goal.Id, null, "Clarify spec",
            kind: HumanWaitKind.SpecClarification, evidenceOwner: "acceptance-gate", testNames: ["GateTests"]).Request;
        Assert.False(GroupedAdmissionLandingHold.IsGateSatisfiable(wait));
    }

    [Fact]
    public void Select_LandingHeldCandidate_SelectsOnlyThreeReadyGoals()
    {
        var candidates = Candidates(out _, out _);
        var selected = Assert.IsType<ConductorMergeTrainSelection>(ConductorMergeTrainSelector.Select(candidates));
        Assert.Equal(candidates.Skip(1).Select(candidate => candidate.GoalId),
            selected.Members.Select(member => member.GoalId));
        Assert.Null(ConductorMergeTrainSelector.Select(candidates[..3]));
    }

    [Fact]
    public void Plan_LandingHeldCandidate_NamesRequestAndExcludesMember()
    {
        var candidates = Candidates(out var held, out var wait);
        var receipt = ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates).FormatReceipt(1);

        Assert.Contains($"exclusions={held.Id.Value[..8]}:LandingHeld(request={wait.Id.Value[..8]})", receipt);
        var members = receipt.Split("members=", StringSplitOptions.None)[1].Split(' ')[0].Split(',');
        Assert.DoesNotContain(held.Id.Value[..8], members);
        Assert.Equal(candidates.Skip(1).Select(candidate => candidate.GoalId.Value[..8]), members);
    }

    private static ConductorSpeculativeAcceptanceCandidate[] Candidates(out Goal held, out HumanInputRequest wait)
    {
        var (kernel, goal) = Setup();
        held = goal;
        wait = Request(kernel, goal, "operator");
        return [new(goal.Id, GroupedAdmissionLandingHold.Apply(Ready(goal.Id), kernel.GetPendingHumanInput(goal.Id))),
            .. Enumerable.Range(0, 3).Select(_ => GoalId.New())
                .Select(id => new ConductorSpeculativeAcceptanceCandidate(id, Ready(id)))];
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) Setup()
    {
        var kernel = new AgentOrchestratorKernel();
        return (kernel, GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents,
            "Grouped admission projection"));
    }

    private static HumanInputRequest Request(AgentOrchestratorKernel kernel, Goal goal, string? owner,
        bool namesTests = true) => kernel.RequestHumanInputDeduplicated(goal.Id, null,
            $"Evidence from {owner ?? "unspecified owner"}", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: owner, testNames: namesTests ? ["GateTests"] : []).Request;

    private static GateReadyCandidateProjectionResult.Ready Ready(GoalId id) => new(new(
        id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, [$"src/{id.Value}.cs"], [$"resource:{id.Value}"],
        new GateReadyMergeEvidence(new string('b', 40), new string('a', 40),
            GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)));
}
