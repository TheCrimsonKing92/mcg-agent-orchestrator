using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLandingTickDurability : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLandingTickDurability(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void SingleGoalLandingIsSavedBeforeSelfRelaunchReload()
    {
        var kernel = new AgentOrchestratorKernel();
        // The earlier active goal has no parallel result. A landing during the
        // pre-walk parallel phase schedules relaunch before this goal is walked.
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Existing worker");
        var goal = CreateVerifiedSimpleGoal(kernel, "Update conductor loop");
        var attemptRoot = CreateTempDirectory("mcg-single-landing-tick-durability");
        var stopFilePath = Path.Combine(attemptRoot, "stop");
        try
        {
            const string landedHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string mainHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective, ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: landedHead);

            var persisted = kernel.ExportSnapshot();
            var landed = false;
            var relaunchCalls = 0;
            GoalStatus? reloadedStatus = null;
            CriterionEvidenceState? reloadedObligationState = null;
            string? reloadedCandidateSha = null;
            Exception? reloadException = null;
            var driver = MakeDriver(
                getFacts: _ => landed
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
                new AcceptanceVerificationSummary(true, [], BranchHeadSha: landedHead, MainHeadSha: mainHead),
                land: candidate =>
                {
                    landed = true;
                    kernel.CompleteGoal(candidate.Id, "Landing, recording, and cleanup completed.");
                    return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed", landedHead);
                },
                getLandingFileScopes: _ =>
                    ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot, runInline: true),
            resolveAcceptanceHeads: _ => (landedHead, mainHead));

            var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    if (relaunchCalls > 1)
                    {
                        throw new InvalidOperationException("Self-relaunch was invoked more than once.");
                    }
                    try
                    {
                        var reloaded = AgentOrchestratorKernel.FromSnapshot(persisted);
                        var reloadedGoal = reloaded.GetGoal(goal.Id);
                        reloadedStatus = reloadedGoal.Status;
                        var obligation = reloadedGoal.CriterionEvidenceObligations.Single();
                        reloadedObligationState = obligation.State;
                        reloadedCandidateSha = obligation.CandidateSha;
                    }
                    catch (Exception exception)
                    {
                        reloadException = exception;
                    }
                    var handoff = new ConductorLoopHandoffResult(
                        Started: true, ProcessId: null, StdoutPath: null, StderrPath: null,
                        Reason: "fixture reload observed");
                    return new ConductorSelfRelaunchResult(
                        handoff.Started, handoff.Started ? null : "handoff", handoff.Reason, handoff);
                },
                selfRelaunchEnabled: true).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, stopFilePath,
                    maxIterations: 3,
                    sleepFunc: _ =>
                    {
                        File.WriteAllText(stopFilePath, "stop");
                        return true;
                    },
                    persistGoalTick: (checkpoint, changedIds) =>
                    {
                        if (changedIds.Contains(goal.Id)) persisted = checkpoint.ExportSnapshot();
                    }));

            Assert.True(landed);
            Assert.Contains("LOOP_RELAUNCH_SCHEDULED", output, StringComparison.Ordinal);
            Assert.Equal(1, relaunchCalls);
            Assert.Null(reloadException);
            Assert.Equal(GoalStatus.Completed, reloadedStatus);
            Assert.Equal(CriterionEvidenceState.Satisfied, reloadedObligationState);
            Assert.Equal(landedHead, reloadedCandidateSha);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void CohortLandingAddsBothMembersToTickChangedGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var ordinary = CreateVerifiedSimpleGoal(kernel, "Operator evidence excludes this goal from cohort");
        var first = CreateVerifiedSimpleGoal(kernel, "First cohort member");
        var second = CreateVerifiedSimpleGoal(kernel, "Second cohort member");
        kernel.RecordGoalRefinement(ordinary.Id, new RefinedSpec(
            ordinary.Objective, ["Operator confirms landing"], VerificationClass.RealWorldDependent, [], []));
        kernel.MapCriterionEvidenceOwner(ordinary.Id, 0, 1, CriterionEvidenceOwner.Operator,
            "operator", "operator:real-world", expectedCandidateSha: ordinary.Id.Value.PadRight(40, 'b')[..40]);
        const string mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [ordinary.Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/OrdinaryTests.cs"],
            [first.Id] = ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CohortMemberTests.cs"]
        };
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(goalId.Value.PadRight(40, 'b')[..40], mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        IReadOnlyList<GoalId>? selectedMembers = null;
        var savedGoalIds = new HashSet<GoalId>();
        ConductorDriver? driver = null;
        driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id],
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector,
            resolveAcceptanceHeads: goal => (goal.Id.Value.PadRight(40, 'b')[..40], mainRevision),
            runAcceptanceCohort: (selection, goals, policy) =>
            {
                selectedMembers = selection.Members.Select(member => member.GoalId).ToArray();
                foreach (var member in selectedMembers)
                {
                    kernel.CompleteGoal(member, "Cohort landing, recording, and cleanup completed.");
                    driver!.SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(
                        member.Value, paths[member], mainRevision));
                }
                return new ConductorAcceptanceCohortRunResult(
                    null,
                    goals.Where(goal => selectedMembers.Contains(goal.Id)).ToDictionary(
                        goal => goal.Id.Value,
                        goal => new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8],
                            policy.Name, new ConductorAdvanceOutcome.Executed(
                                GoalLifecycleState.Verified, "cohort landed")),
                        StringComparer.Ordinal),
                    "outcome=passed");
            });

        new ConductorBatchLoop(
            selfRelaunch: _ =>
            {
                var handoff = new ConductorLoopHandoffResult(
                    Started: true, ProcessId: null, StdoutPath: null, StderrPath: null,
                    Reason: "fixture cohort handoff observed");
                return new ConductorSelfRelaunchResult(true, null, handoff.Reason, handoff);
            },
            selfRelaunchEnabled: true).Run(
                kernel, driver, ConductorAutonomyPolicy.Permissive, NoStopPath(),
                maxIterations: 1,
                persistGoalTick: (_, changedIds) => savedGoalIds.UnionWith(changedIds));

        Assert.NotNull(selectedMembers);
        Assert.Equal(2, selectedMembers.Count);
        Assert.Contains(first.Id, selectedMembers);
        Assert.Contains(second.Id, selectedMembers);
        Assert.Contains(first.Id, savedGoalIds);
        Assert.Contains(second.Id, savedGoalIds);
    }
}
