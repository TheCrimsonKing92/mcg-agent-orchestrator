using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private artifacts, fake holders and launchers, scoped console and census probes.
public sealed class ConductorBatchLoopTestsActivationAdmissionProbeLine(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public void NewAttempt_EachPhaseAppearsBeforeTickEnd(bool grouped, bool fast)
    {
        var root = NewRoot();
        try
        {
            var live = true;
            var holder = new SpawnProcessIdentity(222, DateTimeOffset.UnixEpoch, "C:\\fake-dotnet.exe");
            var generation = new SpawnProcessIdentity(111, DateTimeOffset.UnixEpoch, "C:\\fake-dotnet.exe");
            var lease = new AcceptanceHolderLease(utcNow: () => DateTimeOffset.UnixEpoch,
                classify: _ => live ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
                job: (_, _, _, _) => new AcceptanceJobObservation(live, !live, "probe-fixture"),
                attach: _ => new MemoryStream());
            AcceptanceHolderBinding? binding = null;
            string? result = null;
            string? exit = null;
            var launches = 0;
            var sleeps = 0;
            void Admit()
            {
                if (grouped)
                {
                    var coordinator = new ConductorGroupedGateAttemptCoordinator(root,
                        generationIdentity: generation, holderLease: lease, utcNow: () => DateTimeOffset.UnixEpoch,
                        launch: attempt =>
                        {
                            launches++;
                            using (lease.Claim(AcceptanceHolderBinding.For(attempt), holder)) { }
                            return new(222, holder.StartedAt, holder.ImagePath);
                        });
                    var attempt = coordinator.Launch(coordinator.Create("train", [], "main", "tree", "manifest",
                        "probe-group", root, ConductorAutonomyPolicy.Conservative));
                    binding = AcceptanceHolderBinding.For(attempt);
                    result = attempt.ResultPath;
                    exit = attempt.ExitCodePath;
                }
                else
                {
                    var kernel = new AgentOrchestratorKernel(new FixedClock());
                    var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Probe solo");
                    PassVerificationAt(kernel, goal, goal.Tasks.Single(), DateTimeOffset.UnixEpoch);
                    var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                        generationIdentity: generation, holderLease: lease, utcNow: () => DateTimeOffset.UnixEpoch,
                        isProcessAlive: _ => true, launchOwnedProcess: launch =>
                        {
                            launches++;
                            using (lease.Claim(AcceptanceHolderBinding.For(launch.Attempt), holder)) { }
                            return new(222);
                        });
                    var decision = coordinator.Evaluate(ConductorParallelAcceptanceCandidate.Create(
                        goal, 0, ["src/Probe.cs"], "branch", "main"), ConductorAutonomyPolicy.Conservative, PassingRun);
                    Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
                    var attempt = Assert.IsType<ConductorParallelAcceptanceAttempt>(decision.Attempt);
                    binding = AcceptanceHolderBinding.For(attempt);
                    result = attempt.ResultPath;
                }
                Assert.Equal("Claimed", lease.Read(binding).Phase);
            }
            void Publish()
            {
                File.WriteAllText(result!, grouped ? "{\"Status\":\"failed\"}" : "{\"Kind\":\"Fault\"}");
                if (exit is not null) File.WriteAllText(exit, "1");
            }
            void Release()
            {
                live = false;
                Assert.False(lease.Observe(binding!).ReservesCapacity);
                Assert.Equal("Released", lease.Read(binding!).Phase);
            }
            var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                utcNow: () => DateTimeOffset.UnixEpoch).Run(new AgentOrchestratorKernel(new FixedClock()),
                MakeDriver(), ConductorAutonomyPolicy.Conservative, NoStopPath(), keepAliveWhenIdle: true,
                maxIterations: 8, emitActivationHeartbeat: true, watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    sleeps++;
                    if (sleeps == 1) { Admit(); if (fast) { Publish(); Release(); } }
                    if (sleeps == 2 && !fast) Publish();
                    if (sleeps == 3 && !fast) Release();
                    return sleeps == 4;
                }));
            var lines = Lines(text);
            var samples = ProbeSamples(lines);
            Assert.Equal(new[] { "none", "admitted", "terminal", "released" }, samples.Select(sample => sample.Phase));
            Assert.All(samples.Skip(1), sample => Assert.Equal(binding!.AttemptId, sample.Attempt));
            Assert.Equal(1, launches);
            foreach (var line in lines.Where(line => line.StartsWith(ActivationAdmissionProbeLine.Prefix, StringComparison.Ordinal)))
            {
                var index = Array.IndexOf(lines, line);
                Assert.StartsWith(ActivationAdmissionSample.Prefix, lines[index + 1]);
                Assert.StartsWith("TICK_END tick=1 activation=true ts=", lines[index + 2]);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void InheritedAttempt_WaitingGoal_ReportsNoneOnlyWhenEnabled(bool emit)
    {
        var root = NewRoot();
        try
        {
            var clock = new FixedClock();
            var kernel = new AgentOrchestratorKernel(clock);
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 };
            var goals = Enumerable.Range(0, 2).Select(index =>
            {
                var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), $"Probe {index}");
                PassVerificationAt(kernel, goal, goal.Tasks.Single(), clock.UtcNow);
                return goal;
            }).ToArray();
            var launches = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                utcNow: () => clock.UtcNow, isProcessAlive: _ => true,
                launchOwnedProcess: _ => { launches++; return new(9501); });
            var started = coordinator.Evaluate(ConductorParallelAcceptanceCandidate.Create(goals[0], 0,
                ["src/Probe.cs"], "branch", "main"), policy, PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            kernel.BeginGoalAcceptanceVerification(goals[0].Id, "Acceptance attempt launched.");
            using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
            var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(utcNow: () => clock.UtcNow).Run(
                kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("Width occupied"),
                    getLandingFileScopes: _ => ["src/Probe.cs"], parallelAcceptanceAttemptCoordinator: coordinator),
                policy, NoStopPath(), maxIterations: 1, emitActivationHeartbeat: emit,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => true));
            var lines = Lines(text);
            var samples = ProbeSamples(lines);
            if (emit)
            {
                var sample = Assert.Single(samples);
                Assert.Equal("none", sample.Phase);
                Assert.Equal("none", sample.Attempt);
                Assert.Equal(1, sample.Waiting);
                var probeIndex = Array.FindIndex(lines, line => line.StartsWith(ActivationAdmissionProbeLine.Prefix, StringComparison.Ordinal));
                Assert.StartsWith("TICK_END tick=1 activation=true ts=", lines[probeIndex + 2]);
            }
            else Assert.Empty(samples);
            Assert.Equal(1, launches);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-admission-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static ActivationAdmissionProbeLine[] ProbeSamples(string[] lines) => lines
        .Where(line => line.StartsWith(ActivationAdmissionProbeLine.Prefix, StringComparison.Ordinal))
        .Select(line => { Assert.True(ActivationAdmissionProbeLine.TryParse(line, out var sample)); return sample; }).ToArray();
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}
