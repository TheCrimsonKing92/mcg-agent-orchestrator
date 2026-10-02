using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceStrandedReopen(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public async Task Operator_reopen_after_transient_cap_starts_new_acceptance_without_worker_dispatch()
    {
        var root = CreateTempDirectory("mcg-conductor-stranded-reopen");
        var attemptRoot = Path.Combine(root, "acceptance-gate-attempts");
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Mcg.AgentOrchestrator.App/Orchestration/BaselineDeferral.cs");
        var task = goal.Tasks.Single();
        var acceptanceAttempts = 0;
        var dispatches = 0;
        var attempts = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); },
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceAttempts++;
                if (acceptanceAttempts <= ConductorBatchLoop.ParallelAcceptanceTransientFailureCap)
                    throw new AcceptanceInfrastructureDeferredException(
                        "trusted-main-build-failed", 1, "baseline assembly unavailable");
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/BaselineDeferral.cs"],
            parallelAcceptanceAttemptCoordinator: attempts);

        try
        {
            var capped = new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Conservative, NoStopPath(),
                maxIterations: ConductorBatchLoop.ParallelAcceptanceTransientFailureCap,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false);

            Assert.Equal(1, capped.Escalated);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, acceptanceAttempts);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.All(goal.Tasks, candidate => Assert.Equal(WorkTaskStatus.Completed, candidate.Status));
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Contains(goal.Timeline, evt =>
                evt.TickOutcome?.EscalationKind == nameof(ConductorEscalationKind.BackgroundAcceptanceFailed));
            Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
            Assert.Empty(attempts.GetCapacityReservingAttempts([goal.Id.Value]));
            var previousAttempts = ReadAttempts(attemptRoot, goal);
            Assert.NotEmpty(previousAttempts);
            Assert.All(previousAttempts, attempt =>
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, attempt.Outcome));

            // A restart alone must preserve the hold; only the operator intent may reopen it.
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, sleepFunc: _ => false);
            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, acceptanceAttempts);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(0, dispatches);

            var store = new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            var decisions = CollaborationItemStore.ForDirectory(root);
            var intents = new OperatorIntentCoordinator(store, decisions: decisions,
                goalStateVersionResolver: _ => 7,
                hasLiveAcceptanceAttempt: OperatorIntentCoordinator.BuildLiveAcceptanceAttemptQuery(attempts));
            var id = Guid.NewGuid().ToString("N");
            var payload = new AdjudicateOperatorIntentPayload(
                "reopen-regate", "Re-gate after apparatus recovery", ["receipt-1"], 7, root);
            await store.EnqueueAsync(new OperatorIntentRecord(
                id, id, OperatorIntentVerbs.Adjudicate, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli", "local-process",
                DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));

            var result = intents.ExecutePending(kernel, goal);
            Assert.True(result.MutatedGoalState);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.True(task.LastVerification?.Succeeded);
            Assert.False(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
            var decision = await decisions.GetDecisionStateAsync($"adjudicate-{id}");
            Assert.Equal(EffectReceiptStatus.Applied, decision!.Effect!.Status);
            intents.CompletePersisted([goal.Id]);

            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, sleepFunc: _ => false);

            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap + 1, acceptanceAttempts);
            var previousIds = previousAttempts.Select(attempt => attempt.AttemptId).ToHashSet(StringComparer.Ordinal);
            var fresh = Assert.Single(ReadAttempts(attemptRoot, goal).Where(attempt => !previousIds.Contains(attempt.AttemptId)));
            Assert.Equal(goal.Id.Value, fresh.GoalId);
            Assert.Equal(0, dispatches);
        }
        finally { TryDeleteDirectory(root); }
    }

    private static ConductorParallelAcceptanceAttempt[] ReadAttempts(string root, Goal goal) =>
        Directory.EnumerateFiles(Path.Combine(root, goal.Id.Value), "*.attempt.json")
            .Select(ReadAttempt)
            .ToArray();
}
