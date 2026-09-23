using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class GoalOperationJournalContentionTests
{
    [Xunit.Fact]
    public void GoalOperationJournal_AppendWhileSharedReaderIsOpen_PreservesRawContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-journal-contention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Journal contention");
            GoalOperationJournal.Begin(root, goal, "acceptance", "first");
            var path = GoalOperationJournal.PathFor(root, goal.Id);

            using (new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                GoalOperationJournal.Completed(root, goal, "acceptance", "second");
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"status\":\"Begin\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"detail\":\"first\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"status\":\"Completed\"", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"detail\":\"second\"", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_ReclaimsOnlyExactTerminalInstanceWithinSameProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-reclaim", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "state.db");
            var store = new ReconcileSweepRemediationStore(dbPath);
            var goal = new AgentOrchestratorKernel().CreateGoal("Reclaim terminal same-process lease");
            var instances = new Queue<string>(["first-instance", "second-instance"]);
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => instances.Dequeue());
            var first = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            Xunit.Assert.NotNull(first.Scope);
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var trigger = connection.CreateCommand();
                trigger.CommandText = """
                    CREATE TRIGGER reject_terminal_release
                    BEFORE DELETE ON reconcile_acceptance_leases
                    BEGIN
                        SELECT RAISE(ABORT, 'injected terminal release failure');
                    END;
                    """;
                trigger.ExecuteNonQuery();
            }

            first.Scope!.Complete("terminal before injected release failure");
            first.Scope.Dispose();
            var pending = coordinator.ReadFact(goal);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending, pending?.RecoveryStatus);
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TRIGGER reject_terminal_release";
                drop.ExecuteNonQuery();
            }

            var second = coordinator.TryBegin(goal, "conductor:developer-branch-integration");

            Xunit.Assert.NotNull(second.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Reclaimed, second.LeaseFact?.RecoveryStatus);
            Xunit.Assert.Contains("second-instance", store.TryGetAcceptanceLeaseOwner(goal.Id.Value), StringComparison.Ordinal);
            second.Scope!.Complete("successor completed");
            second.Scope.Dispose();
            Xunit.Assert.Null(store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_ReleasesLeaseWhenBeginAppendFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-begin-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Roll back failed Begin append");
            var blockedExecutionDirectory = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blockedExecutionDirectory, "file blocks journal directory creation");
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                blockedExecutionDirectory,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => "begin-failure-instance");

            _ = Xunit.Assert.ThrowsAny<IOException>(() =>
                coordinator.TryBegin(goal, "conductor:developer-branch-integration"));

            Xunit.Assert.Null(store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_RecoversWhenBeginAndReleaseFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-double-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var innerStore = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Recover failed Begin compensation");
            var journalPath = GoalOperationJournal.PathFor(root, goal.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            File.WriteAllText(journalPath, string.Empty);
            using var journalLock = new FileStream(
                journalPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var store = new FailingReleaseStore(
                innerStore,
                () => throw new IOException("injected compensating release failure"));
            var instances = new Queue<string>(["failed-begin-instance", "successor-instance"]);
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => instances.Dequeue());

            _ = Xunit.Assert.ThrowsAny<IOException>(() =>
                coordinator.TryBegin(goal, "conductor:developer-branch-integration"));

            var pending = coordinator.ReadFact(goal);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending, pending?.RecoveryStatus);
            journalLock.Dispose();
            var recovered = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            Xunit.Assert.NotNull(recovered.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Reclaimed, recovered.LeaseFact?.RecoveryStatus);
            recovered.Scope!.Abort("test cleanup");
            recovered.Scope.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_RecoversWhenTerminalAndReleaseFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-terminal-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var innerStore = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var store = new FailingReleaseStore(
                innerStore,
                () => throw new IOException("injected terminal release failure"));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover failed terminalization");
            var unrelatedGoal = kernel.CreateGoal("Preserve unrelated lease");
            const string unrelatedOwner = "goal-replace:other-owner";
            Xunit.Assert.True(innerStore.TryClaimAcceptanceLease(
                unrelatedGoal.Id.Value,
                unrelatedOwner,
                TimeSpan.FromMinutes(30)));
            var instances = new Queue<string>(["failed-terminal-instance", "successor-instance"]);
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => instances.Dequeue());
            var operation = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            Xunit.Assert.NotNull(operation.Scope);
            var journalPath = GoalOperationJournal.PathFor(root, goal.Id);

            using (var journalLock = new FileStream(
                       journalPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                _ = Xunit.Assert.ThrowsAny<IOException>(() => operation.Scope!.Dispose());
            }

            var pending = coordinator.ReadFact(goal);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending, pending?.RecoveryStatus);
            var recovered = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            Xunit.Assert.NotNull(recovered.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Reclaimed, recovered.LeaseFact?.RecoveryStatus);
            Xunit.Assert.Equal(unrelatedOwner, innerStore.TryGetAcceptanceLeaseOwner(unrelatedGoal.Id.Value));
            recovered.Scope!.Abort("test cleanup");
            recovered.Scope.Dispose();
            Xunit.Assert.Null(innerStore.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_CorruptJournalFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-corrupt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Do not reclaim from corrupt evidence");
            const string operation = "conductor:developer-branch-integration";
            const string instance = "corrupt-instance";
            const string owner = "goal-evidence:v1:conductor:developer-branch-integration:42:corrupt-instance";
            Xunit.Assert.True(store.TryClaimAcceptanceLease(goal.Id.Value, owner, TimeSpan.FromMinutes(30)));
            GoalOperationJournal.Begin(root, goal, operation, operationInstanceId: instance);
            GoalOperationJournal.Completed(root, goal, operation, operationInstanceId: instance);
            File.AppendAllText(
                GoalOperationJournal.PathFor(root, goal.Id),
                "{ malformed journal record" + Environment.NewLine);
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => "contender-instance");

            var blocked = coordinator.TryBegin(goal, operation);

            Xunit.Assert.Null(blocked.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, blocked.LeaseFact?.RecoveryStatus);
            Xunit.Assert.Equal(owner, store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_FailsClosedForLiveMissingAndMalformedEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-hold", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Hold unsafe lease evidence");
            var instances = new Queue<string>(["live-instance", "blocked-instance"]);
            var ownerProcessStartedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
            var pidProbe = new StubConductLockPidProbe(isRunning: true, isSameProcess: true);
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => instances.Dequeue(),
                processStartedAtUtc: () => ownerProcessStartedAt,
                pidProbe: pidProbe);
            using var live = coordinator.TryBegin(goal, "conductor:developer-branch-integration").Scope;

            var blocked = coordinator.TryBegin(goal, "conductor:developer-branch-integration");

            Xunit.Assert.Null(blocked.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Live, blocked.LeaseFact?.RecoveryStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        var missingRoot = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(missingRoot);
        try
        {
            var store = new ReconcileSweepRemediationStore(Path.Combine(missingRoot, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Missing exact journal evidence");
            Xunit.Assert.True(store.TryClaimAcceptanceLease(
                goal.Id.Value,
                "goal-evidence:v1:conductor:developer-branch-integration:42:missing-instance",
                TimeSpan.FromMinutes(30)));
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                missingRoot,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => "contender-instance");

            var blocked = coordinator.TryBegin(goal, "conductor:developer-branch-integration");

            Xunit.Assert.Null(blocked.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, blocked.LeaseFact?.RecoveryStatus);

            store.ReleaseAcceptanceLease(
                goal.Id.Value,
                "goal-evidence:v1:conductor:developer-branch-integration:42:missing-instance");
            Xunit.Assert.True(store.TryClaimAcceptanceLease(
                goal.Id.Value,
                "goal-evidence:malformed",
                TimeSpan.FromMinutes(30)));
            var malformed = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            Xunit.Assert.Null(malformed.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, malformed.LeaseFact?.RecoveryStatus);
        }
        finally
        {
            Directory.Delete(missingRoot, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    public void GoalEvidenceCoordinator_ReclaimsBeginOnlyLeaseWhenOwnerProcessIsAbsentOrReused(
        bool ownerIsRunning,
        bool ownerIsSameProcess)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-owner-gone", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Reclaim abandoned Begin-only evidence lease");
            var instances = new Queue<string>(["abandoned-instance", "successor-instance"]);
            var ownerProcessStartedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
            var pidProbe = new StubConductLockPidProbe(isRunning: true, isSameProcess: true);
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => instances.Dequeue(),
                processStartedAtUtc: () => ownerProcessStartedAt,
                pidProbe: pidProbe);
            var abandoned = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            Xunit.Assert.NotNull(abandoned.Scope);
            var begin = Xunit.Assert.Single(GoalOperationJournal.Read(root, goal.Id).Entries);
            Xunit.Assert.Equal(ownerProcessStartedAt, begin.OwnerProcessStartedAtUtc);
            pidProbe.IsRunningResult = ownerIsRunning;
            pidProbe.IsSameProcessResult = ownerIsSameProcess;

            var recovered = coordinator.TryBegin(goal, "conductor:developer-branch-integration");

            Xunit.Assert.NotNull(recovered.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Reclaimed, recovered.LeaseFact?.RecoveryStatus);
            Xunit.Assert.Equal("reclaimed:abandoned-instance", recovered.LeaseFact?.LatestRecovery);
            Xunit.Assert.Contains("successor-instance", store.TryGetAcceptanceLeaseOwner(goal.Id.Value), StringComparison.Ordinal);
            abandoned.Scope!.Abort("abandoned owner test cleanup");
            abandoned.Scope.Dispose();
            recovered.Scope!.Abort("successor test cleanup");
            recovered.Scope.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_FailsClosedWhenOwnerLivenessIsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-owner-unknown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Hold Begin-only lease when owner liveness is unavailable");
            var instances = new Queue<string>(["live-instance", "blocked-instance"]);
            var pidProbe = new StubConductLockPidProbe(isRunning: true, isSameProcess: true);
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => instances.Dequeue(),
                processStartedAtUtc: () => new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
                pidProbe: pidProbe);
            var live = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            Xunit.Assert.NotNull(live.Scope);
            var heldOwner = store.TryGetAcceptanceLeaseOwner(goal.Id.Value);
            pidProbe.ThrowOnProbe = true;

            var blocked = coordinator.TryBegin(goal, "conductor:developer-branch-integration");

            Xunit.Assert.Null(blocked.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, blocked.LeaseFact?.RecoveryStatus);
            Xunit.Assert.Equal(heldOwner, store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
            pidProbe.ThrowOnProbe = false;
            live.Scope!.Abort("test cleanup");
            live.Scope.Dispose();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_FailsClosedWhenJournalIsUnreadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-unreadable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ReconcileSweepRemediationStore(Path.Combine(root, "state.db"));
            var goal = new AgentOrchestratorKernel().CreateGoal("Unreadable exact journal evidence");
            const string owner = "goal-evidence:v1:conductor:developer-branch-integration:42:locked-instance";
            Xunit.Assert.True(store.TryClaimAcceptanceLease(goal.Id.Value, owner, TimeSpan.FromMinutes(30)));
            GoalOperationJournal.Begin(
                root,
                goal,
                "conductor:developer-branch-integration",
                operationInstanceId: "locked-instance");
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => "contender-instance");
            using var journalLock = new FileStream(
                GoalOperationJournal.PathFor(root, goal.Id),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var blocked = coordinator.TryBegin(goal, "conductor:developer-branch-integration");

            Xunit.Assert.Null(blocked.Scope);
            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, blocked.LeaseFact?.RecoveryStatus);
            Xunit.Assert.Equal(owner, store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalEvidenceCoordinator_ReleasesTerminalOrphanBeforeLaterEvidenceMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-evidence-release", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "state.db");
            var store = new ReconcileSweepRemediationStore(dbPath);
            var goal = new AgentOrchestratorKernel().CreateGoal("Release terminal integration orphan");
            var coordinator = new GoalEvidenceOperationCoordinator(
                store,
                root,
                TimeSpan.FromMinutes(30),
                processId: () => 42,
                instanceId: () => "terminal-instance");
            var operation = coordinator.TryBegin(goal, "conductor:developer-branch-integration");
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var trigger = connection.CreateCommand();
                trigger.CommandText = """
                    CREATE TRIGGER reject_later_mutation_release
                    BEFORE DELETE ON reconcile_acceptance_leases
                    BEGIN
                        SELECT RAISE(ABORT, 'injected release failure');
                    END;
                    """;
                trigger.ExecuteNonQuery();
            }
            operation.Scope!.Complete("integration succeeded before release failure");
            operation.Scope.Dispose();
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TRIGGER reject_later_mutation_release";
                drop.ExecuteNonQuery();
            }

            var recovered = coordinator.TryRecoverTerminal(goal);

            Xunit.Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Reclaimed, recovered?.RecoveryStatus);
            Xunit.Assert.Equal("reclaimed:terminal-instance", recovered?.LatestRecovery);
            Xunit.Assert.Null(store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubConductLockPidProbe(bool isRunning, bool isSameProcess) : IConductLockPidProbe
    {
        public bool IsRunningResult { get; set; } = isRunning;
        public bool IsSameProcessResult { get; set; } = isSameProcess;
        public bool ThrowOnProbe { get; set; }

        public bool IsRunning(int processId)
        {
            if (ThrowOnProbe)
            {
                throw new InvalidOperationException("injected owner liveness failure");
            }

            return IsRunningResult;
        }

        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt)
        {
            if (ThrowOnProbe)
            {
                throw new InvalidOperationException("injected owner identity failure");
            }

            return IsSameProcessResult;
        }
    }

    private sealed class FailingReleaseStore(
        IReconcileSweepRemediationStore inner,
        Action releaseFailure) : IReconcileSweepRemediationStore
    {
        private int _remainingReleaseFailures = 1;

        public ReconcileSweepRemediationState Observe(
            string stateKey,
            string goalId,
            string blockerKind,
            string evidence,
            string remedy) => inner.Observe(stateKey, goalId, blockerKind, evidence, remedy);

        public bool TryMarkBlockerEmitted(string stateKey) => inner.TryMarkBlockerEmitted(stateKey);
        public ReconcileSweepAttemptClaim TryClaimAttempt(string stateKey, int maximumAttempts, string owner) =>
            inner.TryClaimAttempt(stateKey, maximumAttempts, owner);

        public void CompleteAttempt(
            string stateKey,
            string owner,
            int exitStatus,
            string output,
            bool consumeAttempt = true) => inner.CompleteAttempt(stateKey, owner, exitStatus, output, consumeAttempt);

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
            if (Interlocked.Exchange(ref _remainingReleaseFailures, 0) == 1)
            {
                releaseFailure();
            }

            inner.ReleaseAcceptanceLease(goalId, owner);
        }
    }
}
