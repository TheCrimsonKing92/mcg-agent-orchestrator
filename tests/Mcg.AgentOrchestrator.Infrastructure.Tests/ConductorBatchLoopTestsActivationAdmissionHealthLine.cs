using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("DotnetBuildSlots")]
public sealed class ConductorBatchLoopTestsActivationAdmissionHealthLine(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Theory]
    [Xunit.InlineData("reservations")]
    [Xunit.InlineData("live-probe")]
    [Xunit.InlineData("occupied")]
    public void AdmissionOutcome_EmitsHealthImmediatelyBeforeUnchangedTickEnd(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-admission-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FixedClock();
            var kernel = new AgentOrchestratorKernel(clock);
            // One durable attempt must fill the width so the second goal remains waiting.
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 };
            var goals = Enumerable.Range(0, 2).Select(index =>
            {
                var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(),
                    $"Update src/AdmissionHealth{index}.cs");
                PassVerificationAt(kernel, goal, goal.Tasks.Single(), clock.UtcNow);
                return goal;
            }).ToArray();
            var launches = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, TrunkBranchName.Default, utcNow: () => clock.UtcNow,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    Assert.Equal("occupied", scenario);
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9501);
                });
            if (scenario == "reservations")
            {
                var directory = Path.Combine(root, goals[0].Id.Value);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "corrupt.attempt.json"), "{");
                Assert.Throws<InvalidDataException>(() => coordinator.GetAllCapacityReservingAttempts());
            }
            else if (scenario == "occupied")
            {
                var started = coordinator.Evaluate(ConductorParallelAcceptanceCandidate.Create(
                    goals[0], 0, ["src/AdmissionHealth0.cs"], "branch-sha", "main-sha"),
                    policy, PassingRun);
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
                kernel.BeginGoalAcceptanceVerification(goals[0].Id, "Acceptance attempt launched.");
                Assert.Single(coordinator.GetAllCapacityReservingAttempts());
            }
            var probes = 0;
            using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            {
                probes++;
                if (scenario == "live-probe")
                    throw new GateLoadContextProbe.LoadProbeUnavailableException("health-test-census-failed");
                return [];
            });
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("Admission must be held"),
                getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator);
            var sleeps = 0;
            var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(utcNow: () => clock.UtcNow).Run(
                kernel, driver, policy, NoStopPath(),
                maxIterations: 1, emitActivationHeartbeat: true,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => ++sleeps == 1));
            Assert.Equal(1, sleeps);
            var lines = Lines(text);
            var end = Assert.Single(lines.Where(line => line.StartsWith(
                "TICK_END tick=1 activation=true ", StringComparison.Ordinal)));
            // Strip only the existing timestamp suffix to pin the original heartbeat bytes.
            Assert.Equal("TICK_END tick=1 activation=true", end[..end.IndexOf(" ts=", StringComparison.Ordinal)]);
            var healthLine = lines[Array.IndexOf(lines, end) - 1];
            Assert.True(ActivationAdmissionSample.TryParse(healthLine, out var sample), healthLine);
            Assert.Equal(1, sample.Tick);
            Assert.True(sample.WaitingVerified >= 1);
            Assert.Equal(scenario != "occupied", sample.CensusFailed);
            Assert.Equal(0, sample.LiveGates);
            Assert.Single(lines.Where(line => line.StartsWith(ActivationAdmissionSample.Prefix, StringComparison.Ordinal)));
            if (scenario == "occupied") Assert.Equal(policy.AcceptanceWidth, sample.Occupied);
            if (scenario == "live-probe") Assert.True(probes >= 1);
            Assert.Equal(scenario == "occupied" ? 1 : 0, launches);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void IdleTicks_EmitDefaultsOnlyWhenActivationHeartbeatsEnabled(bool emit)
    {
        var sleeps = 0;
        var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
            utcNow: () => DateTimeOffset.UnixEpoch).Run(
            new AgentOrchestratorKernel(new FixedClock()), MakeDriver(),
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 3,
            keepAliveWhenIdle: true, watchInterval: TimeSpan.FromSeconds(1),
            sleepFunc: _ => ++sleeps == 2, emitActivationHeartbeat: emit));
        var lines = Lines(text);
        var health = lines.Where(line => line.StartsWith(ActivationAdmissionSample.Prefix, StringComparison.Ordinal)).ToArray();
        if (!emit)
        {
            Assert.Empty(health);
            Assert.DoesNotContain(lines, line => line.StartsWith("TICK_END ", StringComparison.Ordinal));
            return;
        }
        Assert.Equal(2, health.Length);
        foreach (var line in health)
        {
            Assert.True(ActivationAdmissionSample.TryParse(line, out var sample));
            Assert.Equal(new ActivationAdmissionSample(1, false, 0, 0, 0), sample);
            Assert.StartsWith("TICK_END tick=1 activation=true ts=", lines[Array.IndexOf(lines, line) + 1]);
        }
    }

    private static string[] Lines(string text) => text.Split('\n',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
