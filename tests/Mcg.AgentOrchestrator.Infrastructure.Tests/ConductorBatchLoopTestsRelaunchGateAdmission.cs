using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Serial collection protects the scoped build-root environment override; all artifacts are isolated.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsRelaunchGateAdmission : ConductorBatchLoopTests
{
    private const string RelaunchPath =
        "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs";
    private const string HoldLine = "ADMISSION tick=1 result=held reason=self-relaunch-scheduled";

    public ConductorBatchLoopTestsRelaunchGateAdmission(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void ReconciliationLanding_WithRelaunch_HoldsReadyCohortInSameTick()
    {
        using var fixture = new AdmissionFixture(RelaunchPath, grouped: true);
        var output = fixture.Run();

        var afterScheduled = AfterScheduled(output);
        Assert.Equal(1, CountOccurrences(output, HoldLine));
        Assert.DoesNotContain("ACCEPTANCE_COHORT_ENTRY", afterScheduled, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCEPTANCE_TRAIN", afterScheduled, StringComparison.Ordinal);
        Assert.Equal(0, fixture.CohortCalls);
        Assert.Empty(fixture.GroupedAttempts.ReadAll());
        AssertWaitingGoalsUnattempted(fixture);
    }

    [Xunit.Fact]
    public void ReconciliationLanding_WithRelaunch_HoldsReadySoloInSameTick()
    {
        using var fixture = new AdmissionFixture(RelaunchPath, grouped: false);
        var output = fixture.Run();

        var afterScheduled = AfterScheduled(output);
        Assert.Equal(1, CountOccurrences(output, HoldLine));
        var prefix = fixture.WaitingGoals.Single().Id.Value[..8];
        Assert.DoesNotContain(afterScheduled.Split('\n'), line =>
            line.Contains($"ACCEPTANCE goal={prefix}", StringComparison.Ordinal) &&
            (line.Contains("result=running", StringComparison.Ordinal) ||
             line.Contains("result=started", StringComparison.Ordinal)));
        Assert.Empty(fixture.AcceptanceCalls);
        AssertWaitingGoalsUnattempted(fixture);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ReconciliationLanding_WithDocsOnlyChange_StillAdmitsReadyGates(bool grouped)
    {
        using var fixture = new AdmissionFixture("docs/admission-control.md", grouped);
        var output = fixture.Run();

        Assert.DoesNotContain("LOOP_RELAUNCH_SCHEDULED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=self-relaunch-scheduled", output, StringComparison.Ordinal);
        if (grouped)
        {
            Assert.Contains("ACCEPTANCE_COHORT_ENTRY tick=1", output, StringComparison.Ordinal);
            Assert.Equal(1, fixture.CohortCalls);
            Assert.Single(fixture.GroupedAttempts.ReadAll());
        }
        else
        {
            Assert.Equal(fixture.WaitingGoals.Single().Id, Assert.Single(fixture.AcceptanceCalls));
            Assert.Contains($"ACCEPTANCE goal={fixture.WaitingGoals.Single().Id.Value[..8]}",
                output, StringComparison.Ordinal);
        }
        Assert.Equal(0, fixture.Handoffs);
    }

    [Xunit.Fact]
    public void ReconciliationLanding_WithLiveGroupedGate_PreservesAttemptThroughHandoff()
    {
        using var fixture = new AdmissionFixture(RelaunchPath, grouped: true);
        var members = fixture.WaitingGoals.Select(goal =>
            Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                fixture.Driver.ProjectGateReadyCandidate(goal, fixture.Policy)).Projection).ToArray();
        var selection = new ConductorAcceptanceCohortSelection(members, []);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Driver.TryRegisterCohortGateRunForTests(selection, completion));
        var record = fixture.GroupedAttempts.Create("cohort", members, AdmissionFixture.MainRevision,
            new string('d', 40), "manifest", "live-cohort", fixture.Root, fixture.Policy) with
        {
            OwnerProcessId = 222,
            OwnerProcessStartedAt = DateTimeOffset.UnixEpoch,
            OwnerExecutablePath = "C:\\dotnet.exe"
        };
        ConductorGroupedGateAttemptCoordinator.Save(record);
        Assert.True(File.Exists(record.MetadataPath));
        Assert.False(completion.Task.IsCompleted);

        var output = fixture.Run(maxIterations: 5);

        _ = AfterScheduled(output);
        Assert.Equal(1, fixture.Handoffs);
        Assert.True(File.Exists(record.MetadataPath));
        Assert.False(completion.Task.IsCompleted);
        Assert.Equal(record.AttemptId, Assert.Single(fixture.GroupedAttempts.ReadAll()).AttemptId);
        Assert.Equal(0, fixture.CohortCalls);
    }

    private static string AfterScheduled(string output)
    {
        const string scheduled = "LOOP_RELAUNCH_SCHEDULED tick=1";
        Assert.Contains(scheduled, output, StringComparison.Ordinal);
        return output[output.IndexOf(scheduled, StringComparison.Ordinal)..];
    }

    private static void AssertWaitingGoalsUnattempted(AdmissionFixture fixture)
    {
        Assert.Empty(fixture.Attempts.GetUnreconciledAttempts(
            fixture.WaitingGoals.Select(goal => goal.Id.Value)));
        foreach (var goal in fixture.WaitingGoals)
        {
            Assert.False(Directory.Exists(Path.Combine(fixture.SoloRoot, goal.Id.Value)));
            Assert.Equal(GoalStatus.Verified, goal.Status);
        }
    }

    private sealed class AdmissionFixture : IDisposable
    {
        internal const string MainRevision = "cccccccccccccccccccccccccccccccccccccccc";
        private readonly ConductorBatchLoopTestsParallelAcceptance.EnvVarScope _buildRoot;
        private readonly IDisposable _liveGateProbe;
        private readonly HashSet<GoalId> _landed = [];
        private readonly Goal _landingGoal;
        private readonly ConductorParallelAcceptanceAttempt _seed;
        internal string Root { get; } = CreateTempDirectory("relaunch-gate-admission");
        internal string SoloRoot => Path.Combine(Root, "solo-attempts");
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal ConductorAutonomyPolicy Policy { get; } =
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2, AcceptanceCohortGatherWindowSeconds = 0 };
        internal Goal[] WaitingGoals { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Attempts { get; }
        internal ConductorGroupedGateAttemptCoordinator GroupedAttempts { get; }
        internal ConductorDriver Driver { get; }
        internal List<GoalId> AcceptanceCalls { get; } = [];
        internal int CohortCalls { get; private set; }
        internal int Handoffs { get; private set; }

        internal AdmissionFixture(string landingPath, bool grouped)
        {
            _buildRoot = new ConductorBatchLoopTestsParallelAcceptance.EnvVarScope(
                DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, Path.Combine(Root, "dotnet"));
            _liveGateProbe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
            _landingGoal = CreateVerifiedSimpleGoal(Kernel, "Land completed acceptance receipt");
            WaitingGoals = Enumerable.Range(0, grouped ? 2 : 1)
                .Select(index => CreateVerifiedSimpleGoal(Kernel, $"Ready admission member {index}")).ToArray();
            var paths = WaitingGoals.ToDictionary(goal => goal.Id,
                goal => (IReadOnlyList<string>)
                    [$"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Member{goal.Id.Value}.cs"]);
            paths[_landingGoal.Id] = [landingPath];
            string Branch(GoalId goalId) => goalId.Value.PadRight(40, 'a');
            Attempts = new ConductorParallelAcceptanceAttemptCoordinator(SoloRoot, TrunkBranchName.Default,
                executionDirectory: Root, utcNow: () => DateTimeOffset.UnixEpoch,
                isProcessAlive: _ => false, runInline: true);
            GroupedAttempts = new ConductorGroupedGateAttemptCoordinator(Path.Combine(Root, "grouped-attempts"),
                generationId: 111, isProcessAlive: pid => pid == 222,
                utcNow: () => DateTimeOffset.UnixEpoch);
            var candidate = ConductorParallelAcceptanceCandidate.Create(_landingGoal, 0,
                paths[_landingGoal.Id], Branch(_landingGoal.Id), MainRevision);
            _seed = Attempts.Evaluate(candidate, Policy, PassingRun).Attempt;
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, _seed.Outcome);
            Assert.Null(_seed.ReconciledAt);
            Assert.True(Kernel.BuildVerificationGate(_landingGoal.Id).IsSatisfied);
            Driver = MakeDriver(
                getFacts: goal => _landed.Contains(goal.Id)
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true,
                        IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                land: goal =>
                {
                    _landed.Add(goal.Id);
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed");
                },
                runAcceptanceWithSlot: (goal, _) =>
                {
                    AcceptanceCalls.Add(goal.Id);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: goal => Kernel.BuildVerificationGate(goal.Id).IsSatisfied,
                parallelAcceptanceAttemptCoordinator: Attempts,
                resolveAcceptanceHeads: goal => (Branch(goal.Id), MainRevision),
                gateReadyCandidateProjector: new GateReadyCandidateProjector(
                    goalId => new GateReadyCandidateRevisionPair(Branch(goalId), MainRevision),
                    goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
                    (_, _, _) => new GateReadyMergeTreeObservation(true)),
                runAcceptanceCohort: grouped ? RunCohort : null,
                executionDirectory: Root);
            foreach (var goal in WaitingGoals)
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.True(Kernel.BuildVerificationGate(goal.Id).IsSatisfied);
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(Driver.ProjectGateReadyCandidate(goal, Policy));
            }
            Assert.Empty(Attempts.GetCapacityReservingAttempts(Kernel.Goals.Select(goal => goal.Id.Value)));
            Assert.Empty(GroupedAttempts.ReadAll());
            if (grouped)
            {
                Assert.NotNull(ConductorAcceptanceCohortSelector.Select(WaitingGoals.Select(goal =>
                    new ConductorSpeculativeAcceptanceCandidate(goal.Id,
                        Driver.ProjectGateReadyCandidate(goal, Policy))).ToArray()).Selection);
            }
        }

        private ConductorAcceptanceCohortRunResult RunCohort(ConductorAcceptanceCohortSelection selection,
            IReadOnlyList<Goal> goals, ConductorAutonomyPolicy policy)
        {
            CohortCalls++;
            Assert.Equal(WaitingGoals.Select(goal => goal.Id).OrderBy(id => id.Value),
                selection.Members.Select(member => member.GoalId).OrderBy(id => id.Value));
            var record = GroupedAttempts.Create("cohort", selection.Members, MainRevision,
                new string('d', 40), "manifest", "queued-cohort", Root, policy);
            ConductorGroupedGateAttemptCoordinator.Save(record);
            return new ConductorAcceptanceCohortRunResult(null,
                selection.Members.ToDictionary(member => member.GoalId.Value,
                    member => new ConductorAdvanceResult(member.GoalId.Value, member.GoalId.Value[..8],
                        policy.Name, new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "cohort admitted"))),
                "outcome=passed");
        }

        internal string Run(int maxIterations = 1)
        {
            var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    Handoffs++;
                    return new ConductorSelfRelaunchResult(true, null, null,
                        ConductorLoopHandoffResult.StartedProcess(333, "out", "err"));
                }, selfRelaunchEnabled: true, utcNow: () => DateTimeOffset.UnixEpoch).Run(
                    Kernel, Driver, Policy, Path.Combine(Root, "stop"), maxIterations: maxIterations));
            Assert.Contains(_landingGoal.Id, _landed);
            Assert.NotNull(ReadAttempt(_seed.MetadataPath).ReconciledAt);
            return output;
        }

        public void Dispose()
        {
            _liveGateProbe.Dispose();
            _buildRoot.Dispose();
            TryDeleteDirectory(Root);
        }
    }
}
