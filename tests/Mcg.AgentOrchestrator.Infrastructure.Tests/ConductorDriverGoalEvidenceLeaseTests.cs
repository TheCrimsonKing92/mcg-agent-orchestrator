using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverGoalEvidenceLeaseTests
{
    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void DeveloperIntegrationBusyReleaseReclaimsAcrossTicksWithoutOverlap()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        var dbPath = Path.Combine(root, "state.db");
        var store = new ReconcileSweepRemediationStore(dbPath);
        var (kernel, goal) = SimpleGoal("Recover same-process integration lease");
        var instances = new Queue<string>(["faulted-instance", "overlap-instance", "reclaimed-instance"]);
        var coordinator = new GoalEvidenceOperationCoordinator(
            store,
            root,
            TimeSpan.FromMinutes(30),
            processId: () => 42,
            instanceId: () => instances.Dequeue(),
            processStartedAtUtc: () => new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
            pidProbe: new LiveOwnerPidProbe());
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER reject_cross_tick_release
                BEFORE DELETE ON reconcile_acceptance_leases
                BEGIN
                    SELECT RAISE(ABORT, 'injected post-acquisition release failure');
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        var activeIntegrations = 0;
        var maximumConcurrentIntegrations = 0;
        var integrationCalls = 0;
        var dispatchStarted = false;
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "unused",
            dispatchAndStart: _ =>
            {
                dispatchStarted = true;
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                GoalWorktrees.BranchName(goal.Id),
                "current",
                [],
                null),
            land: (candidate, _) => new LandingResult(
                candidate.Id.Value,
                candidate.Id.Value[..8],
                new LandingDecision.Promote(),
                LandingExecutor.IntegrationBranchName,
                MainAdvanced: true,
                "landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null,
            integrateMainBeforeDeveloperDispatch: candidate =>
            {
                var current = Interlocked.Increment(ref activeIntegrations);
                maximumConcurrentIntegrations = Math.Max(maximumConcurrentIntegrations, current);
                try
                {
                    integrationCalls++;
                    if (integrationCalls == 1)
                    {
                        var overlap = coordinator.TryBegin(candidate, "conductor:developer-branch-integration");
                        Assert.Null(overlap.Scope);
                        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Live, overlap.LeaseFact?.RecoveryStatus);
                        throw new InvalidOperationException("injected conductor tick fault");
                    }

                    return new DeveloperBranchIntegrationResult(
                        DeveloperBranchIntegrationStatus.Current,
                        "integration current after reclaim",
                        []);
                }
                finally
                {
                    Interlocked.Decrement(ref activeIntegrations);
                }
            },
            tryBeginDeveloperIntegrationEvidenceOperation: candidate =>
                coordinator.TryBegin(candidate, "conductor:developer-branch-integration"));

        var fault = Assert.Throws<InvalidOperationException>(() =>
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive));
        Assert.Equal("injected conductor tick fault", fault.Message);
        Assert.False(dispatchStarted);
        Assert.Equal(
            GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending,
            coordinator.ReadFact(goal)?.RecoveryStatus);
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER reject_cross_tick_release";
            drop.ExecuteNonQuery();
        }

        var recovered = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(recovered.Outcome);
        Assert.True(dispatchStarted);
        Assert.Equal(2, integrationCalls);
        Assert.Equal(1, maximumConcurrentIntegrations);
        Assert.Null(store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        Assert.Contains(
            GoalOperationJournal.Read(root, goal.Id).Entries,
            entry => entry.OperationInstanceId == "reclaimed-instance" &&
                entry.Status == GoalOperationStatus.Begin &&
                entry.LeaseRecovery == "reclaimed:faulted-instance");
    }

    [Xunit.Theory]
    [Xunit.InlineData("success", "Completed")]
    [Xunit.InlineData("rejected", "Failed")]
    [Xunit.InlineData("cancelled", "Aborted")]
    public void DeveloperIntegrationTerminalizesBeforeLeaseRelease(
        string scenario,
        string expectedStatus)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        var innerStore = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
        var (kernel, goal) = SimpleGoal($"Terminal ordering {scenario}");
        GoalOperationStatus? statusObservedAtRelease = null;
        var store = new ReleaseObservingRemediationStore(innerStore, (_, owner) =>
        {
            var instance = owner.Split(':')[^1];
            statusObservedAtRelease = GoalOperationJournal.Read(root, goal.Id).Entries
                .LastOrDefault(entry => entry.OperationInstanceId == instance)?.Status;
        });
        var coordinator = new GoalEvidenceOperationCoordinator(
            store,
            root,
            TimeSpan.FromMinutes(30),
            processId: () => 42,
            instanceId: () => $"{scenario}-instance");
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "unused",
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                GoalWorktrees.BranchName(goal.Id),
                "current",
                [],
                null),
            land: (candidate, _) => new LandingResult(
                candidate.Id.Value,
                candidate.Id.Value[..8],
                new LandingDecision.Promote(),
                LandingExecutor.IntegrationBranchName,
                MainAdvanced: true,
                "landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null,
            integrateMainBeforeDeveloperDispatch: _ => scenario switch
            {
                "success" => new DeveloperBranchIntegrationResult(
                    DeveloperBranchIntegrationStatus.Current,
                    "integration current",
                    []),
                "rejected" => new DeveloperBranchIntegrationResult(
                    DeveloperBranchIntegrationStatus.Conflict,
                    "integration rejected",
                    ["src/conflict.cs"]),
                "cancelled" => throw new OperationCanceledException("integration cancelled"),
                _ => throw new InvalidOperationException($"Unknown scenario '{scenario}'.")
            },
            tryBeginDeveloperIntegrationEvidenceOperation: candidate =>
                coordinator.TryBegin(candidate, "conductor:developer-branch-integration"));

        if (scenario == "cancelled")
        {
            _ = Assert.Throws<OperationCanceledException>(() =>
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive));
        }
        else
        {
            _ = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        }

        Assert.Equal(expectedStatus, statusObservedAtRelease?.ToString());
        Assert.Null(innerStore.TryGetAcceptanceLeaseOwner(goal.Id.Value));
    }

    private sealed class LiveOwnerPidProbe : IConductLockPidProbe
    {
        public bool IsRunning(int processId) => true;

        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt) => true;
    }

    private sealed class ReleaseObservingRemediationStore(
        IReconcileSweepRemediationStore inner,
        Action<string, string> beforeRelease) : IReconcileSweepRemediationStore
    {
        public ReconcileSweepRemediationState Observe(
            string stateKey,
            string goalId,
            string blockerKind,
            string evidence,
            string remedy) => inner.Observe(stateKey, goalId, blockerKind, evidence, remedy);

        public bool TryMarkBlockerEmitted(string stateKey) => inner.TryMarkBlockerEmitted(stateKey);

        public ReconcileSweepAttemptClaim TryClaimAttempt(string stateKey, int maximumAttempts, string owner) =>
            inner.TryClaimAttempt(stateKey, maximumAttempts, owner);

        public void CompleteAttempt(string stateKey, string owner, int exitStatus, string output, bool consumeAttempt = true) =>
            inner.CompleteAttempt(stateKey, owner, exitStatus, output, consumeAttempt);

        public bool TryMarkEscalationEmitted(string stateKey) => inner.TryMarkEscalationEmitted(stateKey);

        public bool TryClaimAcceptanceLease(string goalId, string owner, TimeSpan staleAfter) =>
            inner.TryClaimAcceptanceLease(goalId, owner, staleAfter);

        public bool TryReplaceAcceptanceLease(string goalId, string expectedOwner, string successorOwner) =>
            inner.TryReplaceAcceptanceLease(goalId, expectedOwner, successorOwner);

        public IDisposable? TryAcquireAcceptanceLease(string goalId, string owner, TimeSpan staleAfter) =>
            inner.TryAcquireAcceptanceLease(goalId, owner, staleAfter);

        public ReconcileAcceptanceLeaseState? TryGetAcceptanceLease(string goalId, TimeSpan staleAfter) =>
            inner.TryGetAcceptanceLease(goalId, staleAfter);

        public string? TryGetAcceptanceLeaseOwner(string goalId) => inner.TryGetAcceptanceLeaseOwner(goalId);

        public void ReleaseAcceptanceLease(string goalId, string owner)
        {
            beforeRelease(goalId, owner);
            inner.ReleaseAcceptanceLease(goalId, owner);
        }
    }
}
