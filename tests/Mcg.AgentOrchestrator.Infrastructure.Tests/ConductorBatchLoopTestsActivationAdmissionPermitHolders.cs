using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("DotnetBuildSlots")]
public sealed class ConductorBatchLoopTestsActivationAdmissionPermitHolders(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Theory]
    [Xunit.InlineData("pre-review-evidence:", 2)]
    [Xunit.InlineData("worker-build:", 2)]
    [Xunit.InlineData("parallel-acceptance:", 0)]
    [Xunit.InlineData("PRE-REVIEW-EVIDENCE:", 0)]
    public void OccupiedTick_CountsOnlyEvidenceAndWorkerHolders(string prefix, int expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-admission-holders-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedClock();
            var kernel = new AgentOrchestratorKernel(clock);
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 };
            var goals = Enumerable.Range(0, 2).Select(index =>
            {
                var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(),
                    $"Update src/AdmissionHealth{index}.cs");
                PassVerificationAt(kernel, goal, goal.Tasks.Single(), clock.UtcNow);
                return goal;
            }).ToArray();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, TrunkBranchName.Default, utcNow: () => clock.UtcNow,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9501));
            var started = coordinator.Evaluate(ConductorParallelAcceptanceCandidate.Create(
                goals[0], 0, ["src/AdmissionHealth0.cs"], "branch-sha", "main-sha"), policy, PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            kernel.BeginGoalAcceptanceVerification(goals[0].Id, "Acceptance attempt launched.");
            using var liveProbe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
            var probes = 0;
            using var holderProbe = ActivationAdmissionPermitHolders.PushHolderLabelProbe(() =>
            {
                probes++;
                return [prefix + "goal-0be61e6b:attempt-0be61e6b-0-20261025065651585-724c4e1be18a49fba4ccfad94e8ce386",
                    prefix + "goal-90598328:attempt-90598328-0-20261025065020264-f2f41903c8924ace8b93f43aec3aec2c"];
            });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("Admission must be held"),
                getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator);
            var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(utcNow: () => clock.UtcNow).Run(
                kernel, driver, policy, NoStopPath(), maxIterations: 1, emitActivationHeartbeat: true,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => true));
            var line = Assert.Single(text.Split('\n', StringSplitOptions.TrimEntries).Where(
                line => line.StartsWith(ActivationAdmissionSample.Prefix, StringComparison.Ordinal)));
            Assert.True(ActivationAdmissionSample.TryParse(line, out var sample), line);
            Assert.Equal(1, sample.Occupied);
            Assert.Equal(0, sample.LiveGates);
            Assert.True(sample.WaitingVerified >= 1);
            Assert.False(sample.CensusFailed);
            Assert.Equal(expected, sample.EvidenceHeldPermits);
            Assert.True(probes >= 1);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact]
    public void IdleTick_DoesNotReadPermitHolders()
    {
        var probes = 0;
        using var holderProbe = ActivationAdmissionPermitHolders.PushHolderLabelProbe(() => { probes++; return []; });
        var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(utcNow: () => DateTimeOffset.UnixEpoch).Run(
            new AgentOrchestratorKernel(new FixedClock()), MakeDriver(), ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 1, emitActivationHeartbeat: true));
        Assert.Equal(0, probes);
        var line = Assert.Single(text.Split('\n', StringSplitOptions.TrimEntries).Where(
            line => line.StartsWith(ActivationAdmissionSample.Prefix, StringComparison.Ordinal)));
        Assert.True(ActivationAdmissionSample.TryParse(line, out var sample));
        Assert.Equal(0, sample.EvidenceHeldPermits);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
