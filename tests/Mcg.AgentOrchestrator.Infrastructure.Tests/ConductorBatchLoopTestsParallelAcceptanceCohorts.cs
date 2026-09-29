using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceCohorts : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsParallelAcceptanceCohorts(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact]
    public void NonAcceptanceObligationSkipsTrainAndPairCohortButUsesSoloPath()
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(1, 3)
            .Select(index => CreateVerifiedSimpleGoal(kernel, $"Criterion evidence admission member {index}"))
            .ToArray();
        const string mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var branchRevisions = goals.ToDictionary(
            goal => goal.Id,
            goal => goal.Id.Value.PadRight(40, 'b')[..40]);
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [goals[0].Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/BlockedMember.cs"],
            [goals[1].Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/CohortMember.razor"],
            [goals[2].Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Api/CohortMember.cs"]
        };
        kernel.RecordGoalRefinement(goals[0].Id, new RefinedSpec(
            goals[0].Objective,
            ["Operator confirms the landed behavior"],
            VerificationClass.RealWorldDependent,
            [],
            []));
        kernel.MapCriterionEvidenceOwner(
            goals[0].Id,
            0,
            1,
            CriterionEvidenceOwner.Operator,
            "operator",
            "operator:real-world",
            expectedCandidateSha: branchRevisions[goals[0].Id]);
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(branchRevisions[goalId], mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        IReadOnlyList<GoalId>? cohortMembers = null;
        var trainCalls = 0;
        var ordinaryGoals = new List<GoalId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (goal, _) =>
            {
                ordinaryGoals.Add(goal.Id);
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id],
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector,
            resolveAcceptanceHeads: goal => (branchRevisions[goal.Id], mainRevision),
            runMergeTrain: (_, _, _) =>
            {
                trainCalls++;
                return new ConductorMergeTrainRunResult(null, new Dictionary<string, ConductorAdvanceResult>(), [], "unexpected train");
            },
            runAcceptanceCohort: (selection, _, policy) =>
            {
                cohortMembers = selection.Members.Select(member => member.GoalId).ToArray();
                return new ConductorAcceptanceCohortRunResult(
                    null,
                    selection.Members.ToDictionary(
                        member => member.GoalId.Value,
                        member => new ConductorAdvanceResult(
                            member.GoalId.Value,
                            member.GoalId.Value[..8],
                            policy.Name,
                            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "admitted by cohort")),
                        StringComparer.Ordinal),
                    "outcome=passed");
            });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Permissive,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(0, trainCalls);
        Assert.Equal(goals[1..].Select(goal => goal.Id), cohortMembers);
        Assert.Equal([goals[0].Id], ordinaryGoals);
        Assert.Equal(2, summary.Advanced);
        Assert.Equal(1, summary.Held);
    }

    [Xunit.Fact]
    public void LiveCensusFailureAfterAttemptBlocksFurtherAdmissionAndRecordsReason()
    {
        using var isolatedRoot = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        using var release = new ManualResetEventSlim(false);
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 2)
            .Select(index => CreateVerifiedSimpleGoal(
                kernel,
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/CensusFailure{index}.cs"))
            .ToArray();
        var attemptRoot = CreateTempDirectory("mcg-acceptance-census-failure");
        Action waitForAttempts = () => { };
        var probeCalls = 0;

        try
        {
            var coordinator = ConductorBatchLoopTestsParallelAcceptance.ThreadedAcceptanceAttemptCoordinator(
                attemptRoot,
                out waitForAttempts);
            using var liveGateProbe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            {
                if (Interlocked.Increment(ref probeCalls) == 1)
                {
                    return [];
                }

                throw new GateLoadContextProbe.LoadProbeUnavailableException("test-refresh-unavailable");
            });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    release.Wait(TestContext.Current.CancellationToken);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                    [$"src/Mcg.AgentOrchestrator.App/Orchestration/{goal.Id.Value[..8]}.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator);
            BatchTickSummary? tick = null;

            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 },
                NoStopPath(),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.Single(coordinator.GetCapacityReservingAttempts(goals.Select(goal => goal.Id.Value)));
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("detail=live-census-unavailable", StringComparison.Ordinal) &&
                line.Contains("test-refresh-unavailable", StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void LiveCensusFailureAfterCohortBlocksOrdinaryAdmissionAndRecordsReason()
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 3)
            .Select(index => CreateVerifiedSimpleGoal(kernel, $"Census failure cohort member {index}"))
            .ToArray();
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [goals[0].Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/CensusFailureFirst.cs"],
            [goals[1].Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CensusFailureSecond.cs"],
            [goals[2].Id] = ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/CensusFailureThird.cs"]
        };
        const string mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        var cohortCalls = 0;
        var ordinaryCalls = 0;
        var probeCalls = 0;
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
        {
            if (Interlocked.Increment(ref probeCalls) == 1)
            {
                return [];
            }

            throw new GateLoadContextProbe.LoadProbeUnavailableException("test-cohort-refresh-unavailable");
        });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                ordinaryCalls++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id],
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector,
            runAcceptanceCohort: (selection, _, policy) =>
            {
                cohortCalls++;
                return new ConductorAcceptanceCohortRunResult(
                    null,
                    selection.Members.ToDictionary(
                        member => member.GoalId.Value,
                        member => new ConductorAdvanceResult(
                            member.GoalId.Value,
                            member.GoalId.Value[..8],
                            policy.Name,
                            new ConductorAdvanceOutcome.Executed(
                                GoalLifecycleState.Verified,
                                "cohort completed before census refresh")),
                        StringComparer.Ordinal),
                    "outcome=passed");
            });
        BatchTickSummary? tick = null;

        new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 },
            NoStopPath(),
            maxIterations: 1,
            onTick: current => tick = current);

        Assert.Equal(1, cohortCalls);
        Assert.Equal(0, ordinaryCalls);
        Assert.Contains(tick!.ProgressLines!, line =>
            line.Contains("detail=live-census-unavailable", StringComparison.Ordinal) &&
            line.Contains("test-cohort-refresh-unavailable", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CompletedCohortRootWithFreshHeartbeatHoldsTrainAndNamesLiveProcess()
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 3)
            .Select(index => CreateVerifiedSimpleGoal(kernel, $"Completed cohort train member {index}"))
            .ToArray();
        const string mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [goals[0].Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/CompletedCohortTrainFirst.cs"],
            [goals[1].Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CompletedCohortTrainSecond.cs"],
            [goals[2].Id] = ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/CompletedCohortTrainThird.cs"]
        };
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        var trainCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id],
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector,
            runMergeTrain: (selection, _, policy) =>
            {
                trainCalls++;
                return new ConductorMergeTrainRunResult(
                    null,
                    selection.Members.ToDictionary(
                        member => member.GoalId.Value,
                        member => new ConductorAdvanceResult(
                            member.GoalId.Value,
                            member.GoalId.Value[..8],
                            policy.Name,
                            new ConductorAdvanceOutcome.Executed(
                                GoalLifecycleState.Verified,
                                "unexpected train admission")),
                        StringComparer.Ordinal),
                    [],
                    "outcome=passed");
            });
        driver.PublishCompletedCohortGateRunForTests(CohortSelection(goals[0], goals[1]), fault: null);
        var lifecycleCapacity = driver.GetActiveAcceptanceCohortCapacity();
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4101, goals[0].Id.Value, 0, TimeSpan.Zero)]);
        BatchTickSummary? tick = null;

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
            NoStopPath(),
            maxIterations: 1,
            onTick: current => tick = current);

        Assert.Equal(0, lifecycleCapacity.ActiveRootCount);
        Assert.Equal(0, trainCalls);
        Assert.Equal(3, summary.Held);
        Assert.Contains(tick!.ProgressLines!, line =>
            line.Contains("acceptance_width_1_reached", StringComparison.Ordinal) &&
            line.Contains($"goal:{goals[0].Id.Value[..8]}", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void OrdinaryAttemptHeartbeatWithoutReservationHoldsCohort()
    {
        const string goalId = "87654321-ordinary-attempt";
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 2)
            .Select(index => CreateVerifiedSimpleGoal(kernel, $"Heartbeat cohort member {index}"))
            .ToArray();
        const string mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [goals[0].Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/HeartbeatFirst.razor"],
            [goals[1].Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Api/HeartbeatSecond.cs"]
        };
        var projector = new GateReadyCandidateProjector(
            candidateGoalId => new GateReadyCandidateRevisionPair(
                candidateGoalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            candidateGoalId => new GateReadyLandingScopeObservation(true, paths[candidateGoalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        var cohortCalls = 0;
        var ordinaryCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                ordinaryCalls++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id],
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector,
            runAcceptanceCohort: (selection, _, policy) =>
            {
                cohortCalls++;
                return new ConductorAcceptanceCohortRunResult(
                    null,
                    selection.Members.ToDictionary(
                        member => member.GoalId.Value,
                        member => new ConductorAdvanceResult(
                            member.GoalId.Value,
                            member.GoalId.Value[..8],
                            policy.Name,
                            new ConductorAdvanceOutcome.Executed(
                                GoalLifecycleState.Verified,
                                "unexpected cohort admission")),
                        StringComparer.Ordinal),
                    "outcome=passed");
            });
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4102, goalId, 1, TimeSpan.Zero)]);
        BatchTickSummary? tick = null;

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
            NoStopPath(),
            maxIterations: 1,
            onTick: current => tick = current);

        Assert.Equal(0, cohortCalls);
        Assert.Equal(0, ordinaryCalls);
        Assert.Equal(2, summary.Held);
        Assert.Contains(tick!.ProgressLines!, line =>
            line.Contains("acceptance_width_1_reached", StringComparison.Ordinal) &&
            line.Contains("goal:87654321", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void StaleHeartbeatDoesNotOccupyAcceptanceCapacity()
    {
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(
                4103,
                "stale123-goal",
                0,
                GateLoadContextProbe.LiveGateHeartbeatFreshness + TimeSpan.FromSeconds(1))]);

        var census = BuildCensusFromProbe();
        var decision = ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, width: 1);

        Assert.Equal(0, census.OccupiedCount);
        Assert.True(decision.IsAdmitted);
    }

    [Xunit.Fact]
    public void CensusDescriptionIsIdenticalAcrossAttemptCohortAndTrainOccupants()
    {
        const string goalId = "abcdef12-shared-occupant";
        var attempt = CreateCensusAttempt(goalId, processId: 4104);
        var ordinary = ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [attempt],
            new HashSet<string>([attempt.AttemptId], StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot([]),
            []);
        var cohort = ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [],
            new HashSet<string>(StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot(
                [new ConductorAcceptanceCapacityRoot("cohort-root", new HashSet<string>([goalId], StringComparer.Ordinal))]),
            []);
        var train = ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [],
            new HashSet<string>(StringComparer.Ordinal),
            new ConductorAcceptanceCapacitySnapshot([]),
            [new GateLoadContextProbe.LiveGateOccupant(4104, goalId, 0, TimeSpan.Zero)]);

        Assert.Equal(ordinary.Describe(), cohort.Describe());
        Assert.Equal(ordinary.Describe(), train.Describe());
    }

    [Xunit.Fact]
    public void ProductionCohortRunsOneSharedGateAndBypassesOrdinaryMemberGates()
    {
        var root = CreateTempDirectory("mcg-speculative-cohort-advisory");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "Update first advisory source");
        var second = CreateVerifiedSimpleGoal(kernel, "Update second advisory source");
        var statusesBefore = new[] { first.Status, second.Status };
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/FirstAdvisory.razor"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondAdvisoryTests.cs"]
        };
        var acceptanceCalls = new List<string>();
        var landed = new List<string>();
        var cohortCalls = 0;
        string? sharedReceiptId = null;
        var mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    acceptanceCalls.Add(goal.Id.Value);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "ok");
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runAcceptanceCohort: (selection, goals, policy) =>
                {
                    cohortCalls++;
                    var identity = AcceptanceCohortIdentity.Create(
                        selection.BindMembers(),
                        mainRevision,
                        "dddddddddddddddddddddddddddddddddddddddd",
                        "manifest-v1");
                    var receipt = new AcceptanceCohortReceipt(
                        $"receipt-{identity.Value}",
                        identity,
                        AcceptanceCohortGateOutcome.Passed,
                        DateTimeOffset.UtcNow,
                        10,
                        [],
                        GateExitCode: 0,
                        GateTestResultPaths: [Path.GetFullPath("production-cohort.trx")],
                        ValidForLanding: true);
                    sharedReceiptId = receipt.ReceiptId;
                    return new ConductorAcceptanceCohortRunResult(
                        receipt,
                        goals.Where(goal => selection.Members.Any(member => member.GoalId == goal.Id)).ToDictionary(
                            goal => goal.Id.Value,
                            goal => new ConductorAdvanceResult(
                                goal.Id.Value,
                                goal.Id.Value[..8],
                                policy.Name,
                                new ConductorAdvanceOutcome.Executed(
                                    GoalLifecycleState.Verified,
                                    $"shared receipt {receipt.ReceiptId}")),
                            StringComparer.Ordinal),
                        $"outcome=passed receipt={receipt.ReceiptId}");
                });

            var summary = new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            var cohortEvents = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "speculative-cohort-plan")
                .ToArray();

            Assert.Equal(2, summary.Advanced);
            Assert.Equal(1, cohortCalls);
            Assert.Empty(acceptanceCalls);
            Assert.Empty(landed);
            Assert.NotNull(sharedReceiptId);
            Assert.Equal(statusesBefore, new[] { first.Status, second.Status });
            var receipt = Assert.Single(cohortEvents);
            Assert.Null(receipt.GoalId);
            Assert.Contains("advisory=true", receipt.Detail, StringComparison.Ordinal);
            Assert.Contains(first.Id.Value[..8], receipt.Detail, StringComparison.Ordinal);
            Assert.Contains(second.Id.Value[..8], receipt.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void CohortExit_InfrastructureFailureEmitsReason()
    {
        var root = CreateTempDirectory("mcg-cohort-infrastructure-reason");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "First infrastructure failure member");
        var second = CreateVerifiedSimpleGoal(kernel, "Second infrastructure failure member");
        var statusesBefore = new[] { first.Status, second.Status };
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/FirstInfrastructureFailure.razor"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondInfrastructureFailureTests.cs"]
        };
        var mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                land: goal => new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "ok"),
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runAcceptanceCohort: (selection, goals, policy) =>
                {
                    var identity = AcceptanceCohortIdentity.Create(
                        selection.BindMembers(),
                        mainRevision,
                        "dddddddddddddddddddddddddddddddddddddddd",
                        "manifest-v1");
                    var receipt = new AcceptanceCohortReceipt(
                        $"receipt-{identity.Value}",
                        identity,
                        AcceptanceCohortGateOutcome.InfrastructureFailure,
                        DateTimeOffset.UtcNow,
                        10,
                        ["infrastructure tests"],
                        GateExitCode: 2,
                        GateTestResultPaths: [],
                        ValidForLanding: false,
                        InfrastructureReasonCode: AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                        InfrastructureDetail: "Acceptance verification did not produce coherent TRX evidence.");
                    return new ConductorAcceptanceCohortRunResult(
                        receipt,
                        goals.Where(goal => selection.Members.Any(member => member.GoalId == goal.Id)).ToDictionary(
                            goal => goal.Id.Value,
                            goal => new ConductorAdvanceResult(
                                goal.Id.Value,
                                goal.Id.Value[..8],
                                policy.Name,
                                new ConductorAdvanceOutcome.Held(
                                    GoalLifecycleState.Verified,
                                    "shared infrastructure failure receipt")),
                            StringComparer.Ordinal),
                        $"outcome={receipt.Outcome} receipt={receipt.ReceiptId}");
                });

            _ = new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            var exit = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Single(record => record.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT", StringComparison.Ordinal));

            var exitTokens = exit.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("outcome=InfrastructureFailure", exitTokens);
            Assert.Contains("reason=trx-evidence-incoherent", exitTokens);
            Assert.DoesNotContain("reason=trx-evidence-incoherent-extra", exitTokens);
            Assert.Equal(statusesBefore, new[] { first.Status, second.Status });
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void CohortGateFault_HoldsBothMembersAndKeepsTheTickAlive()
    {
        var faulted = RunCohortFaultTicks(CaptureGateEngineFault, runs: 1);
        var control = RunCohortFaultTicks(fault: null, runs: 1);

        Assert.Null(faulted.LoopException);
        var exitTokens = faulted.Lines
            .Single(line => line.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT", StringComparison.Ordinal))
            .Detail
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("outcome=gate-fault", exitTokens);
        Assert.Contains("fault=AcceptanceGateEngineException", exitTokens);

        foreach (var goalId in faulted.MemberGoalIds)
        {
            var memberLine = faulted.Lines.Single(line =>
                line.Detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal) &&
                line.Detail.Contains($"goal={goalId[..8]}", StringComparison.Ordinal));
            Assert.Contains("result=held", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("fault=AcceptanceGateEngineException", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("classification=transient", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("failures=1/3", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("worker-process-registration-failed", memberLine.Detail, StringComparison.Ordinal);
        }

        Assert.Equal(faulted.StatusesBefore, faulted.StatusesAfter);
        Assert.Equal(control.StopReason, faulted.StopReason);
    }

    [Xunit.Fact]
    public void StructuredProtectedBoundaryFaultHoldsWholeCohortWithoutRetry()
    {
        var identity = new ProtectedProcessIdentity(4001, 100);
        var detail = WorkerProcessJobs.BuildProtectedBoundaryRegistrationFailure(4001, identity);
        Exception CaptureFault()
        {
            try { throw new InvalidOperationException(detail); }
            catch (InvalidOperationException exception)
            {
                return AcceptanceGateEngineException.Capture(exception,
                    new AcceptanceGateDiagnosticSnapshot("check-execution", "focused cohort tests"));
            }
        }

        var faulted = RunCohortFaultTicks(CaptureFault, runs: 2);
        Assert.Null(faulted.LoopException);
        Assert.Equal(faulted.StatusesBefore, faulted.StatusesAfter);
        foreach (var goalId in faulted.MemberGoalIds)
        {
            var line = faulted.Lines.Single(record =>
                record.Detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal) &&
                record.Detail.Contains($"goal={goalId[..8]}", StringComparison.Ordinal));
            Assert.Contains("result=escalated", line.Detail, StringComparison.Ordinal);
            Assert.Contains("classification=terminal", line.Detail, StringComparison.Ordinal);
            Assert.Contains("failures=0/3", line.Detail, StringComparison.Ordinal);
            Assert.Contains("pid=4001", line.Detail, StringComparison.Ordinal);
            Assert.Contains("protected=4001@100", line.Detail, StringComparison.Ordinal);
        }
    }

    // The 2026-09-15 daemon exits: the exception was raised on the cohort gate's background thread and
    // reached a later tick through the driver's completion. This drives that arrival end to end.
    [Xunit.Fact]
    public void FaultedBackgroundCohortCompletion_IsResolvedInsideTheTickInsteadOfEndingIt()
    {
        var faulted = RunCohortFaultTicks(
            CaptureGateEngineFault,
            runs: 1,
            arrival: CohortFaultArrival.BackgroundCompletion);
        var control = RunCohortFaultTicks(fault: null, runs: 1);

        Assert.Null(faulted.LoopException);
        var exitTokens = faulted.Lines
            .Single(line => line.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT", StringComparison.Ordinal))
            .Detail
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("outcome=gate-fault", exitTokens);
        Assert.Contains("fault=AcceptanceGateEngineException", exitTokens);
        // The injected cohort runner was on its control-hold path, so the fault the tick resolved came from
        // the driver returning the faulted background completion, not from a throw at the call site.
        Assert.DoesNotContain(
            faulted.Lines,
            line => line.Detail.Contains("outcome=control-hold", StringComparison.Ordinal));

        foreach (var goalId in faulted.MemberGoalIds)
        {
            var memberLine = faulted.Lines.Single(line =>
                line.Detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal) &&
                line.Detail.Contains($"goal={goalId[..8]}", StringComparison.Ordinal));
            Assert.Contains("result=held", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("fault=AcceptanceGateEngineException", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("classification=transient", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("failures=1/3", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("worker-process-registration-failed", memberLine.Detail, StringComparison.Ordinal);
        }

        Assert.Equal(faulted.StatusesBefore, faulted.StatusesAfter);
        Assert.Equal(control.StopReason, faulted.StopReason);
    }

    [Xunit.Fact]
    public void FaultedBackgroundCohortCompletion_EscalatesBothMembersOnlyAtTheTransientFailureCap()
    {
        var faulted = RunCohortFaultTicks(
            CaptureGateEngineFault,
            runs: ConductorBatchLoop.ParallelAcceptanceTransientFailureCap,
            arrival: CohortFaultArrival.BackgroundCompletion);

        Assert.Null(faulted.LoopException);
        var memberLines = faulted.Lines
            .Where(line => line.Detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal))
            .Select(line => line.Detail)
            .ToArray();
        Assert.Contains(memberLines, detail =>
            detail.Contains("result=held", StringComparison.Ordinal) &&
            detail.Contains("failures=1/3", StringComparison.Ordinal));
        foreach (var goalId in faulted.MemberGoalIds)
        {
            Assert.Contains(memberLines, detail =>
                detail.Contains($"goal={goalId[..8]}", StringComparison.Ordinal) &&
                detail.Contains("result=escalated", StringComparison.Ordinal) &&
                detail.Contains("failures=3/3", StringComparison.Ordinal));
        }
    }

    [Xunit.Fact]
    public void CohortGateFault_EscalatesBothMembersOnlyAtTheTransientFailureCap()
    {
        var faulted = RunCohortFaultTicks(
            CaptureGateEngineFault,
            runs: ConductorBatchLoop.ParallelAcceptanceTransientFailureCap);

        Assert.Null(faulted.LoopException);
        var memberLines = faulted.Lines
            .Where(line => line.Detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal))
            .Select(line => line.Detail)
            .ToArray();
        Assert.Contains(memberLines, detail =>
            detail.Contains("result=held", StringComparison.Ordinal) &&
            detail.Contains("failures=1/3", StringComparison.Ordinal));
        Assert.DoesNotContain(memberLines, detail =>
            detail.Contains("result=escalated", StringComparison.Ordinal) &&
            detail.Contains("failures=1/3", StringComparison.Ordinal));
        foreach (var goalId in faulted.MemberGoalIds)
        {
            Assert.Contains(memberLines, detail =>
                detail.Contains($"goal={goalId[..8]}", StringComparison.Ordinal) &&
                detail.Contains("result=escalated", StringComparison.Ordinal) &&
                detail.Contains("failures=3/3", StringComparison.Ordinal));
        }
    }

    [Xunit.Fact]
    public void CohortGateFault_NonTransientFaultEscalatesBothMembersWithoutEndingTheTick()
    {
        var faulted = RunCohortFaultTicks(
            () => new InvalidOperationException("Acceptance cohort workspace ownership was already transferred."),
            runs: 1);

        Assert.Null(faulted.LoopException);
        foreach (var goalId in faulted.MemberGoalIds)
        {
            var memberLine = faulted.Lines.Single(line =>
                line.Detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal) &&
                line.Detail.Contains($"goal={goalId[..8]}", StringComparison.Ordinal));
            Assert.Contains("result=escalated", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("classification=non-transient", memberLine.Detail, StringComparison.Ordinal);
            Assert.Contains("fault=InvalidOperationException", memberLine.Detail, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void FaultedBackgroundCohortCompletion_ReturnsTypedFaultInsteadOfThrowing()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "First background fault member");
        var second = CreateVerifiedSimpleGoal(kernel, "Second background fault member");
        var driver = MakeDriver();
        var selection = CohortSelection(first, second);
        var fault = CaptureGateEngineFault();
        driver.PublishCompletedCohortGateRunForTests(selection, fault);

        ConductorAcceptanceCohortRunOutcome? outcome = null;
        var thrown = Record.Exception(() => outcome = driver.RunAcceptanceCohortForTick(
            selection,
            [first, second],
            ConductorAutonomyPolicy.Conservative,
            runGateInBackground: true));

        Assert.Null(thrown);
        Assert.NotNull(outcome);
        Assert.NotNull(outcome!.Fault);
        Assert.Same(fault, outcome.Fault!.Fault);
        Assert.Equal("AcceptanceGateEngineException", outcome.Fault.FaultType);
        Assert.Contains("worker-process-registration-failed", outcome.Fault.Message, StringComparison.Ordinal);
        var result = outcome.Run;
        Assert.Contains("outcome=gate-fault", result.Detail, StringComparison.Ordinal);
        Assert.Contains("fault=AcceptanceGateEngineException", result.Detail, StringComparison.Ordinal);
        Assert.Equal(2, result.MemberResults.Count);
        Assert.All(
            result.MemberResults.Values,
            member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));

        // The fault is drained once: the next call no longer reports it and resumes the ordinary path,
        // which for this dependency-free test driver is the production-dependency guard.
        var afterDrain = Record.Exception(() => driver.RunAcceptanceCohortForTick(
            selection,
            [first, second],
            ConductorAutonomyPolicy.Conservative,
            runGateInBackground: true));
        Assert.IsType<InvalidOperationException>(afterDrain);
        Assert.Contains("Production acceptance cohort dependencies", afterDrain.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FaultedBackgroundCohortCompletion_IsDrainedBeforeDifferentPairSelection()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "First completed background member");
        var second = CreateVerifiedSimpleGoal(kernel, "Second completed background member");
        var third = CreateVerifiedSimpleGoal(kernel, "Newly selected cohort member");
        var driver = MakeDriver();
        var completedSelection = CohortSelection(first, second);
        var currentSelection = CohortSelection(second, third);
        var fault = CaptureGateEngineFault();
        driver.PublishCompletedCohortGateRunForTests(completedSelection, fault);

        ConductorAcceptanceCohortRunOutcome? outcome = null;
        var thrown = Record.Exception(() => outcome = driver.RunAcceptanceCohortForTick(
            currentSelection,
            [first, second, third],
            ConductorAutonomyPolicy.Conservative,
            runGateInBackground: true));

        Assert.Null(thrown);
        Assert.NotNull(outcome);
        Assert.NotNull(outcome!.Fault);
        Assert.Same(fault, outcome.Fault!.Fault);
        Assert.Equal(
            new[] { first.Id.Value, second.Id.Value }.OrderBy(value => value, StringComparer.Ordinal),
            outcome.Run.MemberResults.Keys.OrderBy(value => value, StringComparer.Ordinal));
        Assert.DoesNotContain(third.Id.Value, outcome.Run.MemberResults.Keys);
    }

    // How the cohort gate fault reaches the tick: thrown by the cohort run itself, or parked by a background
    // cohort gate thread whose completion the driver observes on a later tick.
    private enum CohortFaultArrival
    {
        SynchronousThrow,
        BackgroundCompletion
    }

    private sealed record CohortFaultTickObservation(
        Exception? LoopException,
        string? StopReason,
        IReadOnlyList<ConductEventRecord> Lines,
        IReadOnlyList<string> MemberGoalIds,
        IReadOnlyList<GoalStatus> StatusesBefore,
        IReadOnlyList<GoalStatus> StatusesAfter);

    // Drives production cohort selection into a gate run that faults. A null fault is the held control.
    // With CohortFaultArrival.BackgroundCompletion the cohort run itself never throws: a completed gate run
    // whose completion carries the exception is published on the driver first, exactly as the background
    // gate thread's SetException leaves it, and the tick has to reach that fault through the driver's own
    // drain. A faulted tick ends the loop with no progress, so repeat faults are driven as successive
    // one-tick runs over the same driver, which is where the per-pair transient-fault count lives.
    private static CohortFaultTickObservation RunCohortFaultTicks(
        Func<Exception>? fault,
        int runs,
        CohortFaultArrival arrival = CohortFaultArrival.SynchronousThrow)
    {
        var root = CreateTempDirectory("mcg-cohort-gate-fault");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "First gate fault member");
        var second = CreateVerifiedSimpleGoal(kernel, "Second gate fault member");
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/FirstGateFault.razor"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondGateFaultTests.cs"]
        };
        var mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));

        try
        {
            var statusesBefore = new[]
            {
                kernel.GetGoal(first.Id).Status,
                kernel.GetGoal(second.Id).Status
            };
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                land: goal => new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "ok"),
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                // The synchronous-throw arrival throws from here; the background arrival leaves this runner
                // on the control-hold path, so a tick that reports a gate fault can only have got it from
                // the driver's own observation of the faulted background completion.
                runAcceptanceCohort: (selection, goals, policy) =>
                    fault is null || arrival == CohortFaultArrival.BackgroundCompletion
                    ? new ConductorAcceptanceCohortRunResult(
                        Receipt: null,
                        goals
                            .Where(goal => selection.Members.Any(member => member.GoalId == goal.Id))
                            .ToDictionary(
                                goal => goal.Id.Value,
                                goal => new ConductorAdvanceResult(
                                    goal.Id.Value,
                                    goal.Id.Value[..8],
                                    policy.Name,
                                    new ConductorAdvanceOutcome.Held(
                                        GoalLifecycleState.Verified,
                                        "control hold")),
                                StringComparer.Ordinal),
                        "outcome=control-hold")
                    : throw fault());

            var backgroundSelection = new ConductorAcceptanceCohortSelection(
                [
                    ReadyProjection(
                        first.Id,
                        first.Id.Value.PadRight(40, 'b')[..40],
                        mainRevision,
                        paths[first.Id][0]),
                    ReadyProjection(
                        second.Id,
                        second.Id.Value.PadRight(40, 'b')[..40],
                        mainRevision,
                        paths[second.Id][0])
                ],
                []);

            BatchLoopSummary? summary = null;
            Exception? loopException = null;
            for (var run = 0; run < runs && loopException is null; run++)
            {
                if (fault is not null && arrival == CohortFaultArrival.BackgroundCompletion)
                {
                    driver.PublishCompletedCohortGateRunForTests(backgroundSelection, fault());
                }

                loopException = Record.Exception(() => summary = new ConductorBatchLoop(
                    conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1));
            }

            var lines = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            return new CohortFaultTickObservation(
                loopException,
                summary?.StopReason,
                lines,
                [first.Id.Value, second.Id.Value],
                statusesBefore,
                [kernel.GetGoal(first.Id).Status, kernel.GetGoal(second.Id).Status]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static ConductorAcceptanceCohortSelection CohortSelection(Goal first, Goal second)
    {
        var mainRevision = new string('c', 40);
        return new ConductorAcceptanceCohortSelection(
            [
                ReadyProjection(first.Id, new string('a', 40), mainRevision, "src/First.cs"),
                ReadyProjection(second.Id, new string('b', 40), mainRevision, "tests/SecondTests.cs")
            ],
            []);
    }

    private static ConductorBatchLoop.LiveAcceptanceCensus BuildCensusFromProbe(
        ConductorAcceptanceCapacitySnapshot? activeCohorts = null) =>
        ConductorBatchLoop.BuildLiveAcceptanceCensus(
            [],
            new HashSet<string>(StringComparer.Ordinal),
            activeCohorts ?? new ConductorAcceptanceCapacitySnapshot([]),
            GateLoadContextProbe.CaptureLiveGateOccupants());

    private static ConductorParallelAcceptanceAttempt CreateCensusAttempt(string goalId, int processId) =>
        new(
            "attempt-1",
            goalId,
            goalId[..8],
            0,
            "branch",
            "main",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            processId,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            "stdout",
            "stderr",
            "exit",
            "heartbeat",
            "result",
            "metadata");

    private static GateReadyCandidateProjection ReadyProjection(
        GoalId goalId,
        string branchRevision,
        string mainRevision,
        string landingPath) =>
        new(
            goalId,
            GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied,
            ChangeRiskTier.DocsOnly,
            ConductorTransitionDecision.Auto,
            [landingPath],
            [$"production:{goalId.Value[..8]}"],
            new GateReadyMergeEvidence(
                branchRevision,
                mainRevision,
                GateReadyMergeStatus.Clean,
                GateReadyMergeReason.NoConflictsDetected));

    // The production shape of the 2026-09-15 daemon exits: a worker-process registration refusal
    // captured as a gate-engine fault on the cohort gate's background thread.
    private static AcceptanceGateEngineException CaptureGateEngineFault()
    {
        try
        {
            throw new InvalidOperationException(
                "worker-process-registration-failed pid=38220 stage=protected-process-boundary " +
                "cleanup=refused-protected-process");
        }
        catch (InvalidOperationException exception)
        {
            return AcceptanceGateEngineException.Capture(
                exception,
                new AcceptanceGateDiagnosticSnapshot("check-execution", "focused cohort tests"));
        }
    }
}
