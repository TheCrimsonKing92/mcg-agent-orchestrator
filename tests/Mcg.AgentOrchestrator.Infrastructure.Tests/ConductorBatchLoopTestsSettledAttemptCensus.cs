using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSettledAttemptCensus(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ThreeTicks_SkipHistoricalAttempts_AndReleaseCandidateSlotOnExit()
    {
        using var liveGates = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var root = CreateTempDirectory("mcg-loop-settled-census");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
            var kernel = new AgentOrchestratorKernel(new FixedClock(now));
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Update src/Census.cs");
            PassVerificationAt(kernel, goal, Assert.Single(goal.Tasks), now);
            var legacy = WriteAttempt(goal.Id.Value, "legacy");
            var settled = Enumerable.Range(0, 12).Select(index => WriteAttempt($"historical-{index}", $"settled-{index}")).ToArray();
            var historicalPaths = settled.Select(attempt => attempt.MetadataPath).ToHashSet(StringComparer.Ordinal);
            foreach (var attempt in settled)
                Assert.True(LegacyAcceptanceHolderSettlement.RecordTerminal(AcceptanceHolderBinding.For(attempt),
                    attempt.OwnerProcessId, attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath,
                    attempt.StartedAt, now, "fixture-holder-exited"));

            var candidateStatus = SpawnTrackedProcessStatus.LiveMatch;
            var reads = new List<string>();
            var historicalClassifications = 0;
            var launches = 0;
            var holder = new SpawnProcessIdentity(2_000_000_003, now, "fixture-holder.exe");
            var lease = new AcceptanceHolderLease(utcNow: () => now,
                classify: _ => SpawnTrackedProcessStatus.LiveMatch, attach: _ => new EmptyDisposable(),
                job: (_, _, _, _) => new(true, false, "fixture-live-holder"));
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                utcNow: () => now, classifyHolder: attempt =>
                {
                    if (attempt.AttemptId == legacy.AttemptId) return candidateStatus;
                    historicalClassifications++;
                    Assert.Contains(attempt.MetadataPath, historicalPaths);
                    return SpawnTrackedProcessStatus.DeadOrRecycled;
                }, holderLease: lease, generationIdentity: new(2_000_000_001, now, "fixture-conductor.exe"),
                hostBootTime: () => null, acquireStableSlotLease: (_, _) => null,
                attemptMetadataReadObserver: reads.Add,
                launchOwnedProcess: launch =>
                {
                    Assert.Equal(SpawnTrackedProcessStatus.DeadOrRecycled, candidateStatus);
                    Assert.True(File.Exists(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(AcceptanceHolderBinding.For(legacy))));
                    launches++;
                    using var claim = lease.Claim(AcceptanceHolderBinding.For(launch.Attempt), holder);
                    return new(holder.ProcessId, holder.StartedAt, holder.ImagePath);
                });
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("Expected owned launch seam."),
                getLandingFileScopes: _ => ["src/Census.cs"], parallelAcceptanceAttemptCoordinator: coordinator,
                resolveAcceptanceHeads: _ => ("branch-sha", "main-sha"), utcNow: () => now);
            Assert.True(driver.ParallelAcceptanceEnabled);
            var ticks = new List<BatchTickSummary>();
            var readSnapshots = new List<string[]>();
            var classificationSnapshots = new List<int>();
            var launchSnapshots = new List<int>();
            new ConductorBatchLoop(utcNow: () => now).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 3, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
                onTick: tick =>
                {
                    ticks.Add(tick);
                    readSnapshots.Add(reads.ToArray());
                    classificationSnapshots.Add(historicalClassifications);
                    launchSnapshots.Add(launches);
                    reads.Clear();
                    historicalClassifications = 0;
                    if (tick.Tick == 2) candidateStatus = SpawnTrackedProcessStatus.DeadOrRecycled;
                });

            Assert.Equal(new[] { 1, 2, 3 }, ticks.Select(tick => tick.Tick));
            Assert.Equal(new[] { 0, 0, 1 }, launchSnapshots);
            for (var index = 0; index < 3; index++)
            {
                Assert.Contains(ticks[index].ProgressLines!, line =>
                    line.StartsWith($"ACCEPTANCE goal={goal.Id.Value[..8]} ", StringComparison.Ordinal) &&
                    line.Contains("slot=slot-0", StringComparison.Ordinal) &&
                    line.Contains(index < 2 ? "result=running" : "result=started", StringComparison.Ordinal));
                Assert.DoesNotContain(ticks[index].ProgressLines!, line => line.Contains("result=escalated", StringComparison.Ordinal));
            }
            Assert.All(historicalPaths, path => Assert.Contains(path, readSnapshots[0]));
            Assert.True(classificationSnapshots[0] > 0);
            for (var index = 1; index < 3; index++)
            {
                Assert.DoesNotContain(readSnapshots[index], historicalPaths.Contains);
                Assert.Equal(0, classificationSnapshots[index]);
                Assert.Contains(legacy.MetadataPath, readSnapshots[index]);
            }
            var successor = Assert.Single(coordinator.GetAllCapacityReservingAttempts());
            Assert.Equal(0, successor.SlotIndex);
            Assert.NotEqual(legacy.AttemptId, successor.AttemptId);
            Assert.Equal(1, launches);

            ConductorParallelAcceptanceAttempt WriteAttempt(string goalId, string id)
            {
                var directory = Path.Combine(root, goalId);
                Directory.CreateDirectory(directory);
                var attempt = new ConductorParallelAcceptanceAttempt(id, goalId, goalId[..8], 0,
                    "branch-sha", "main-sha", now.AddDays(-2), now.AddDays(-1), 2_000_000_002,
                    ConductorParallelAcceptanceAttemptOutcome.Passed,
                    Path.Combine(directory, id + ".out"), Path.Combine(directory, id + ".err"),
                    Path.Combine(directory, id + ".exit"), Path.Combine(directory, id + ".heartbeat"),
                    Path.Combine(directory, id + ".result"), Path.Combine(directory, id + ".attempt.json"),
                    ReconciledAt: now.AddDays(-1))
                { OwnerProcessStartedAt = now.AddDays(-2), OwnerExecutablePath = "fixture-holder.exe" };
                File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return attempt;
            }
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
