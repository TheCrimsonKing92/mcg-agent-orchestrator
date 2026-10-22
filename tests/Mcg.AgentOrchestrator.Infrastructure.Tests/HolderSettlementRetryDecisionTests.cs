using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

// Parallel-safe: the test owns its temporary root and injects every driver operation.
public sealed class HolderSettlementRetryDecisionTests
{
    [Fact]
    public void HolderSettlementDeathUsesBoundedRetryPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"holder-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Holder settlement retry policy");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
            var escalations = new List<(Goal Goal, GoalLifecycleState State, string Reason)>();
            var driver = new ConductorDriver(
                getFacts: _ => throw Unexpected(),
                getRunningPaidWorkerCount: () => throw Unexpected(),
                createWorkspace: _ => throw Unexpected(),
                dispatchAndStart: _ => throw Unexpected(),
                startRecordedDispatches: _ => throw Unexpected(),
                buildServerShutdown: () => throw Unexpected(),
                runAcceptanceVerification: _ => throw Unexpected(),
                runAdvisorySemanticAcceptance: (_, _) => throw Unexpected(),
                retryTask: (_, _, _) => throw Unexpected(),
                recordTaskNote: (_, _, _) => throw Unexpected(),
                recordCriterionRetryFeedback: (_, _, _) => throw Unexpected(),
                clearCriterionRetryFeedback: (_, _) => throw Unexpected(),
                rebaseOntoMain: _ => throw Unexpected(),
                land: (_, _) => throw Unexpected(),
                afterSuccessfulLanding: (_, _) => throw Unexpected(),
                record: _ => throw Unexpected(),
                cleanup: _ => throw Unexpected(),
                writeEscalation: (g, state, reason) => escalations.Add((g, state, reason)),
                classifyChangeRisk: _ => throw Unexpected(),
                utcNow: () => DateTimeOffset.UnixEpoch,
                executionDirectory: root);
            var directory = Path.Combine(root, goal.Id.Value);
            Directory.CreateDirectory(directory);
            var holder = new SpawnProcessIdentity(222, DateTimeOffset.UnixEpoch, "dotnet.exe");
            var generation = new SpawnProcessIdentity(111, DateTimeOffset.UnixEpoch, "conductor.exe");
            var running = new ConductorParallelAcceptanceAttempt("attempt", goal.Id.Value,
                goal.Id.Value[..8], 0, "branch", "main", DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, holder.ProcessId, ConductorParallelAcceptanceAttemptOutcome.Running,
                Path.Combine(directory, "out"), Path.Combine(directory, "err"), Path.Combine(directory, "exit"),
                Path.Combine(directory, "heartbeat"), Path.Combine(directory, "result"),
                Path.Combine(directory, "attempt.attempt.json"))
            {
                HolderContractVersion = 1,
                OwnerProcessStartedAt = holder.StartedAt,
                OwnerExecutablePath = holder.ImagePath
            };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllText(running.MetadataPath, JsonSerializer.Serialize(running, json));
            var holderAlive = true;
            var binding = AcceptanceHolderBinding.For(running);
            var lease = new AcceptanceHolderLease(
                utcNow: () => DateTimeOffset.UnixEpoch,
                classify: identity => identity == holder && !holderAlive
                    ? SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch,
                attach: _ => new EmptyDisposable(),
                job: (_, identity, status, cancel) =>
                {
                    Assert.Equal(holder, identity);
                    if (cancel) holderAlive = false;
                    return new(status == SpawnTrackedProcessStatus.LiveMatch, !holderAlive, "test holder");
                });
            lease.Reserve(binding, generation);
            using var claim = lease.Claim(binding, holder);
            Assert.Equal("Claimed", lease.Read(binding).Phase);
            Assert.True(lease.Cancel(binding, generation, "test cancellation"));
            Assert.Equal("Cancelled", lease.Read(binding).Phase);
            Assert.False(File.Exists(running.ResultPath));
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, "integration",
                isProcessAlive: _ => false, utcNow: () => DateTimeOffset.UnixEpoch,
                holderLease: lease, generationIdentity: generation);
            Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
            var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(running.MetadataPath), json)!;
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, attempt.Outcome);
            Assert.Equal("holder-tree-settled:Cancelled", attempt.Detail);
            Assert.Equal(1, attempt.TransientFailureCount);

            var first = ConductorBatchLoop.ParallelAcceptanceTerminal(
                driver, candidate, ConductorAutonomyPolicy.Permissive, attempt);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.StartsWith("Transient background acceptance process-died (1/3)", held.Reason);
            Assert.Empty(escalations);

            var third = ConductorBatchLoop.ParallelAcceptanceTerminal(
                driver, candidate, ConductorAutonomyPolicy.Permissive,
                attempt with { TransientFailureCount = 3 });
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(third.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, escalated.State);
            Assert.Equal(ConductorEscalationKind.BackgroundAcceptanceFailed, escalated.Kind);
            Assert.Equal("background acceptance process-died: holder-tree-settled:Cancelled", escalated.Reason);
            var recorded = Assert.Single(escalations);
            Assert.Equal(goal, recorded.Goal);
            Assert.Equal(escalated.State, recorded.State);
            Assert.Equal(escalated.Reason, recorded.Reason);
        }
        finally
        {
            Directory.Delete(root, true);
        }

        static InvalidOperationException Unexpected() => new("Unexpected driver operation during terminal decision.");
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
