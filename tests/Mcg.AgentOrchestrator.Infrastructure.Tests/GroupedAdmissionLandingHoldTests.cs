using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel and uses only in-memory projections.
public sealed class GroupedAdmissionLandingHoldTests
{
    [Theory]
    [InlineData("developer")]
    [InlineData("worker")]
    public void Apply_ReportWaitWithRecordedReport_ReturnsSameReadyProjection(string owner)
    {
        var (kernel, goal, wait) = ReportSetup(owner, "deferred - AlphaTests");
        var pending = kernel.GetPendingHumanInput(goal.Id);
        var ready = Ready(goal.Id);

        Assert.Same(ready, GroupedAdmissionLandingHold.Apply(ready, pending, goal));
        Assert.Null(GroupedAdmissionLandingHold.FindBlockingWait(pending, goal));
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        var baseline = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
            GroupedAdmissionLandingHold.Apply(ready, pending));
        Assert.Equal(GateReadyCandidateExclusionReason.LandingHeld, baseline.Reason);
        Assert.Equal(wait.Id.Value, baseline.HoldRequestId);
    }

    [Fact]
    public void Plan_ReportWaitWithAndWithoutReport_DrivesSpeculativePlanner()
    {
        foreach (var hasClasses in new[] { true, false })
        {
            var (kernel, goal, wait) = ReportSetup("worker", hasClasses ? "deferred - AlphaTests" : "deferred - ");
            var otherGoals = Enumerable.Range(0, 3).Select(_ => Setup()).ToArray();
            ConductorSpeculativeAcceptanceCandidate[] candidates =
            [
                new(goal.Id, GroupedAdmissionLandingHold.Apply(Ready(goal.Id), kernel.GetPendingHumanInput(goal.Id), goal)),
                .. otherGoals.Select(other => new ConductorSpeculativeAcceptanceCandidate(other.Goal.Id,
                    GroupedAdmissionLandingHold.Apply(Ready(other.Goal.Id), other.Kernel.GetPendingHumanInput(other.Goal.Id), other.Goal)))
            ];

            var receipt = ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates).FormatReceipt(1);
            var members = receipt.Split("members=", StringSplitOptions.None)[1].Split(' ')[0].Split(',');

            if (hasClasses)
            {
                Assert.DoesNotContain("LandingHeld(request=", receipt);
                Assert.Contains(goal.Id.Value[..8], members);
            }
            else
            {
                Assert.Contains($"{goal.Id.Value[..8]}:LandingHeld(request={wait.Id.Value[..8]})", receipt);
                Assert.DoesNotContain(goal.Id.Value[..8], members);
            }
        }
    }

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

    private static (AgentOrchestratorKernel Kernel, Goal Goal, HumanInputRequest Wait) ReportSetup(string owner, string tests)
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var goal = kernel.CreateGoal("Admit a recorded Developer report", [developer]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Role report evidence",
            [RoleReportEvidenceClosureTests.ReportCriterion], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var started = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", "C:\\fixture", started, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "B");
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "implement", "C:\\fixture", 0, RoleReportEvidenceClosureTests.DeveloperOutput(tests), string.Empty, started.AddSeconds(1),
            WorkerResultPresent: true, HasCommittedChanges: true, DispatchStartedAt: started));
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        var wait = kernel.RequestHumanInputDeduplicated(goal.Id, null, "Provide Developer report",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
            plannerIdentity: new PlannerEvidenceIdentity(goal.Id.Value, developer.Id.Value,
                goal.AuthoritativeBrief.Version, 1, "developer-report"),
            plannerEvidence: new PlannerEvidencePayload(1, "developer-report", "post-implementation", owner,
                "Deferred report", "Developer owns; Acceptance executes", null, null, null)).Request;
        return (kernel, goal, wait);
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
