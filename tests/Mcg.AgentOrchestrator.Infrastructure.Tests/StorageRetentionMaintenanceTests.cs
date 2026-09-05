using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class StorageRetentionMaintenanceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
    private const string GoalId = "11111111111111111111111111111111";
    private const string TaskId = "22222222222222222222222222222222";

    [Xunit.Fact(DisplayName = "WorkerRetention_artifact_one_day_inside_threshold_is_retained_as_gzip")]
    public void ArtifactOneDayInsideThresholdIsRetainedAsGzip()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("out.log", Now.AddDays(-13));
        fixture.WriteSuccessfulExit(Now.AddDays(-13));

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".gz"));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_artifact_one_day_outside_threshold_is_removed")]
    public void ArtifactOneDayOutsideThresholdIsRemoved()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteSuccessfulExit(Now.AddDays(-15));

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(path));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_failed_dispatch_log_is_kept_past_threshold")]
    public void FailedDispatchLogIsKeptPastThreshold()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("err.log", Now.AddDays(-45));

        fixture.Run(TerminalGoal(WorkTaskStatus.Failed));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".gz"));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_earlier_failed_dispatch_is_kept_after_task_retry_succeeds")]
    public void EarlierFailedDispatchIsKeptAfterTaskRetrySucceeds()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("err.log", Now.AddDays(-45));
        var exitPath = fixture.WorkerArtifactPath("exit.txt");
        DispatchExitArtifacts.Write(exitPath, DispatchExitArtifacts.Native(1, "failed", Now.AddDays(-45)));
        File.SetLastWriteTimeUtc(exitPath, Now.AddDays(-45).UtcDateTime);

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(exitPath));
    }

    [Xunit.Fact]
    public void WorkerArtifacts_SuccessfulDispatchPastThreshold_RemovesWholeGroup()
    {
        using var fixture = new RetentionFixture();
        var paths = new[]
        {
            fixture.WriteSuccessfulChildExit(Now.AddDays(-15)),
            fixture.WriteWorkerArtifact("err.log", Now.AddDays(-15)),
            fixture.WriteSuccessfulExit(Now.AddDays(-15)),
            fixture.WriteWorkerArtifact("out.log", Now.AddDays(-15))
        };
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.True(DispatchExitArtifacts.TryRead(paths[2], out var exitArtifact));
        Assert.Equal(0, exitArtifact.ExitCode);

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.All(paths, path => Assert.False(File.Exists(path)));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_dispatch_without_exit_artifact_is_kept_past_threshold")]
    public void DispatchWithoutExitArtifactIsKeptPastThreshold()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("err.log", Now.AddDays(-45));

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".gz"));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_goal_with_no_terminal_state_is_untouched")]
    public void GoalWithNoTerminalStateIsUntouched()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("out.log", Now.AddDays(-45));

        fixture.Run(new StorageRetentionGoal(
            GoalId,
            GoalStatus.Active,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Completed }));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".gz"));
    }

    [Xunit.Fact(DisplayName = "StorageRetention_persisted_terminal_goals_are_loaded_outside_conductor_working_set")]
    public async Task PersistedTerminalGoalsAreLoadedOutsideConductorWorkingSet()
    {
        using var fixture = new RetentionFixture();
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(
            Path.Combine(fixture.OrchestratorDirectory, "state.db"));
        await repository.SaveGoalSnapshotsAsync([
            GoalSnapshotFor(GoalId, GoalStatus.Completed, WorkTaskStatus.Completed),
            GoalSnapshotFor("33333333333333333333333333333333", GoalStatus.Active, WorkTaskStatus.Running)
        ]);

        var goals = StorageRetentionMaintenance.LoadPersistedGoals(repository);

        Assert.Equal(2, goals.Count);
        var terminal = Assert.Single(goals, goal => goal.IsTerminal);
        Assert.Equal(GoalId, terminal.GoalId);
        Assert.Equal(WorkTaskStatus.Completed, terminal.TaskStatuses[TaskId]);
        Assert.Contains(goals, goal => goal.Status == GoalStatus.Active);
    }

    [Xunit.Fact(DisplayName = "RunEventRetention_failed_blocked_and_active_goal_evidence_is_not_prunable")]
    public void FailedBlockedAndActiveGoalEvidenceIsNotPrunable()
    {
        var emptyTasks = new Dictionary<string, WorkTaskStatus>();
        var ids = StorageRetentionMaintenance.SelectGoalOperationPrunableIds([
            new StorageRetentionGoal("completed", GoalStatus.Completed, emptyTasks),
            new StorageRetentionGoal("cancelled", GoalStatus.Cancelled, emptyTasks),
            new StorageRetentionGoal("superseded", GoalStatus.Superseded, emptyTasks),
            new StorageRetentionGoal("failed", GoalStatus.Failed, emptyTasks),
            new StorageRetentionGoal("acceptance-failed", GoalStatus.AcceptanceFailed, emptyTasks),
            new StorageRetentionGoal("waiting", GoalStatus.WaitingForHuman, emptyTasks),
            new StorageRetentionGoal("active", GoalStatus.Active, emptyTasks)
        ]);

        Assert.Equal(["completed", "cancelled", "superseded"], ids);
    }

    [Xunit.Fact]
    public async Task Cadence_NonTerminalGoal_OperationRowsSurvivePruning()
    {
        using var fixture = new RetentionFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        const string activeGoalId = "33333333333333333333333333333333";
        await repository.SaveGoalSnapshotsAsync([
            GoalSnapshotFor(GoalId, GoalStatus.Completed, WorkTaskStatus.Completed),
            GoalSnapshotFor(activeGoalId, GoalStatus.Active, WorkTaskStatus.Running)
        ]);
        var runEventPath = Path.Combine(fixture.ExecutionDirectory, "run-events-terminal-filter.db");
        var store = new SqliteRunEventStore(runEventPath);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            GoalId,
            "conductor:test",
            "Completed",
            "terminal operation",
            "{}",
            OccurredAt: Now.AddDays(-45)));
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            activeGoalId,
            "conductor:test",
            "Active",
            "active operation",
            "{}",
            OccurredAt: Now.AddDays(-45)));

        RunEventMaintenanceCadence.TryRunIfDue(
            runEventPath,
            Path.Combine(fixture.LogDirectory, "terminal-filter-conduct.jsonl"),
            () => Now,
            workspace: workspace,
            mtpResultsRootOverride: fixture.MtpResultsRoot);
        var events = await store.ReadSinceAsync(0);

        Assert.DoesNotContain(events, item =>
            item.EventType == RunEventTypes.GoalOperation && item.GoalId == GoalId);
        Assert.Contains(events, item =>
            item.EventType == RunEventTypes.GoalOperation && item.GoalId == activeGoalId);
    }

    [Xunit.Fact(DisplayName = "DispatchDiagnostics_unowned_global_file_is_retained")]
    public void UnownedGlobalDispatchDiagnosticsFileIsRetained()
    {
        using var fixture = new RetentionFixture();
        var path = Path.Combine(fixture.LogDirectory, "dispatch-diagnostics.jsonl");
        File.WriteAllText(path, "legacy diagnostics");

        var result = fixture.Run();

        Assert.True(File.Exists(path));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == path &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.OwnerResolution == EvidenceOwnerResolution.Unrecorded &&
            decision.Reason == "global-diagnostics-preserved-by-policy");
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_sweep_locked_appending_file_is_preserved_without_throwing")]
    public async Task SweepLockedAppendingFileIsPreservedWithoutThrowing()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WorkerArtifactPath("out.log");
        File.WriteAllText(path, "before");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-15).UtcDateTime);
        fixture.WriteSuccessfulExit(Now.AddDays(-15));
        var writerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Position = stream.Length;
            writerReady.SetResult();
            await releaseWriter.Task;
            await stream.WriteAsync("after"u8.ToArray());
        });

        await writerReady.Task;
        try
        {
            StorageRetentionResult? result = null;
            var exception = Record.Exception(() => result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed)));
            Assert.Null(exception);
            Assert.True(File.Exists(path));
            Assert.Contains(result!.Decisions, decision =>
                decision.Path == path &&
                decision.Action == EvidenceRetentionAction.DeferredLocked &&
                decision.FailureExceptionType == nameof(IOException));
        }
        finally
        {
            releaseWriter.SetResult();
            await writer;
        }

        Assert.Equal("beforeafter", File.ReadAllText(path));
    }

    [Xunit.Fact(DisplayName = "AcceptanceRetention_last_failing_attempt_and_summaries_are_preserved")]
    public void LastFailingAttemptAndSummariesArePreserved()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        var failingMetadata = Path.Combine(goalDirectory, "failure.attempt.json");
        var failingLog = Path.Combine(goalDirectory, "failure.err.log");
        var successfulTrx = Path.Combine(goalDirectory, "success.trx");
        fixture.WriteAttempt(goalDirectory, "failure", ordinal: 1, failed: true, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "success", ordinal: 2, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "final", ordinal: 3, failed: false, reconciled: true);
        File.WriteAllText(failingLog, "failure detail");
        File.WriteAllText(successfulTrx, SuccessfulTrx("Suite.Test"));
        SetAge(failingMetadata, 30);
        SetAge(failingLog, 30);
        SetAge(successfulTrx, 30);

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(failingMetadata));
        Assert.True(File.Exists(failingLog));
        Assert.False(File.Exists(successfulTrx));
        var receiptPath = successfulTrx + ".test-identities.json";
        Assert.True(File.Exists(receiptPath));
        Assert.Contains("Suite.Test", File.ReadAllText(receiptPath), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AcceptanceRetention_OlderFailingTrxIsReducedToReceiptWhileLastFailureRemainsWhole()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "old-failure", ordinal: 1, failed: true, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "last-failure", ordinal: 2, failed: true, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "final", ordinal: 3, failed: false, reconciled: true);
        var oldFailureTrx = Path.Combine(goalDirectory, "old-failure.trx");
        var lastFailureTrx = Path.Combine(goalDirectory, "last-failure.trx");
        File.WriteAllText(oldFailureTrx, FailedTrx("Suite.OldFailure"));
        File.WriteAllText(lastFailureTrx, FailedTrx("Suite.LastFailure"));

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(oldFailureTrx));
        Assert.Contains(
            "Suite.OldFailure",
            File.ReadAllText(oldFailureTrx + ".test-identities.json"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Synthetic failure: Suite.OldFailure",
            File.ReadAllText(oldFailureTrx + ".test-identities.json"),
            StringComparison.Ordinal);
        Assert.Contains(
            "at Suite.OldFailure()",
            File.ReadAllText(oldFailureTrx + ".test-identities.json"),
            StringComparison.Ordinal);
        Assert.True(File.Exists(lastFailureTrx));
    }

    [Xunit.Fact]
    public void AcceptanceRetention_LegacyFailingTrxReceiptWithoutFailureSignaturePreservesSource()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "old-failure", ordinal: 1, failed: true, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "last-failure", ordinal: 2, failed: true, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "final", ordinal: 3, failed: false, reconciled: true);
        var oldFailureTrx = Path.Combine(goalDirectory, "old-failure.trx");
        File.WriteAllText(oldFailureTrx, FailedTrx("Suite.OldFailure"));
        File.WriteAllText(
            oldFailureTrx + ".test-identities.json",
            """{"source":"old-failure.trx","testIdentities":[{"name":"Suite.OldFailure","outcome":"Failed"}]}""");

        var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(oldFailureTrx));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == oldFailureTrx &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "trx-is-unreadable-or-has-no-test-identities");
    }

    [Xunit.Fact]
    public void AcceptanceRetention_ByteBound_NeverDeletesFreshEvidence()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "old", ordinal: 1, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "final", ordinal: 2, failed: false, reconciled: true);
        var freshLog = Path.Combine(goalDirectory, "old.out.log");
        File.WriteAllText(freshLog, "fresh evidence");
        File.SetLastWriteTimeUtc(freshLog, Now.AddHours(-1).UtcDateTime);

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [TerminalGoal(WorkTaskStatus.Completed)],
            Now,
            acceptanceArtifactMaxBytesForTests: 1);

        Assert.True(File.Exists(freshLog));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == freshLog &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "byte-bound-unsatisfiable-fresh-evidence");
    }

    [Xunit.Fact]
    public void AcceptanceRetention_WriterLeaseRemainsHeldThroughCandidateDeletion()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "old", ordinal: 1, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "final", ordinal: 2, failed: false, reconciled: true);
        var oldLog = Path.Combine(goalDirectory, "old.out.log");
        File.WriteAllText(oldLog, "old evidence");
        SetAge(oldLog, 30);
        var contenderAcquired = true;

        StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [TerminalGoal(WorkTaskStatus.Completed)],
            Now,
            beforeAttemptCandidateDeletionForTests: directory =>
            {
                var contender = new Thread(() =>
                {
                    using var contenderMutex = new Mutex(false, StorageRetentionMaintenance.AttemptLeaseNameFor(directory));
                    var acquired = false;
                    try
                    {
                        acquired = contenderMutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    finally
                    {
                        if (acquired)
                        {
                            contenderMutex.ReleaseMutex();
                        }
                        contenderAcquired = acquired;
                    }
                });
                contender.IsBackground = true;
                contender.Start();
                Assert.True(contender.Join(TimeSpan.FromSeconds(10)), "Lease contender thread did not finish.");
            });

        Assert.False(contenderAcquired);
        Assert.False(File.Exists(oldLog));
    }

    [Xunit.Fact]
    public void AcceptanceRetention_MismatchedTrxReceiptPreservesSourceTrx()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "old", ordinal: 1, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "final", ordinal: 2, failed: false, reconciled: true);
        var trx = Path.Combine(goalDirectory, "old.trx");
        File.WriteAllText(trx, SuccessfulTrx("Suite.Source"));
        File.WriteAllText(
            trx + ".test-identities.json",
            "{\"source\":\"different.trx\",\"testIdentities\":[{\"name\":\"Suite.Other\"}]}");
        SetAge(trx, 30);

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(trx));
    }

    [Xunit.Fact]
    public void AcceptanceRetention_LockedTrxRetainsOwnerMetadataUntilRetrySucceeds()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        var oldMetadata = fixture.WriteAttempt(goalDirectory, "old", ordinal: 1, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "final", ordinal: 2, failed: false, reconciled: true);
        var trx = Path.Combine(goalDirectory, "old.trx");
        File.WriteAllText(trx, SuccessfulTrx("Suite.RetryableIdentity"));
        SetAge(oldMetadata, 30);

        StorageRetentionResult first;
        using (new FileStream(trx, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            first = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));
        }

        Assert.True(File.Exists(trx));
        Assert.True(File.Exists(oldMetadata));
        Assert.Contains(first.Decisions, decision =>
            decision.Path == oldMetadata &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "retained-test-artifact-owner-metadata");

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(trx));
        Assert.False(File.Exists(oldMetadata));
        Assert.True(File.Exists(trx + ".test-identities.json"));
    }

    [Xunit.Fact]
    public void AcceptanceRetention_CountBoundPrunesOldAttemptAndPreservesTrxIdentityReceipt()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        var oldMetadata = fixture.WriteAttempt(goalDirectory, "old", ordinal: 1, failed: false, reconciled: true);
        for (var ordinal = 2;
             ordinal <= ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal + 2;
             ordinal++)
        {
            fixture.WriteAttempt(goalDirectory, $"attempt-{ordinal:D2}", ordinal, failed: false, reconciled: true);
        }
        var trx = Path.Combine(goalDirectory, "old.trx");
        File.WriteAllText(trx, SuccessfulTrx("Suite.PreservedIdentity"));

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));
        var receipt = trx + ".test-identities.json";
        Assert.False(File.Exists(trx));
        Assert.True(File.Exists(receipt));

        Assert.False(File.Exists(oldMetadata));
        Assert.Equal(
            ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal,
            Directory.GetFiles(goalDirectory, "*.attempt.json", SearchOption.TopDirectoryOnly).Length);
        Assert.True(File.Exists(receipt));
        Assert.Contains("Suite.PreservedIdentity", File.ReadAllText(receipt), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AcceptanceRetention_NonTerminalGoalRetainsEvidenceAndReportsUnboundedPolicy()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        for (var ordinal = 1;
             ordinal <= ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal + 2;
             ordinal++)
        {
            fixture.WriteAttempt(goalDirectory, $"active-{ordinal:D2}", ordinal, failed: false, reconciled: true);
        }
        var oldLog = Path.Combine(goalDirectory, "active-01.out.log");
        File.WriteAllText(oldLog, "active evidence");
        SetAge(oldLog, 30);
        var activeGoal = new StorageRetentionGoal(
            GoalId,
            GoalStatus.Active,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Running });

        var result = fixture.Run(activeGoal);

        Assert.True(File.Exists(oldLog));
        Assert.Equal(
            ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal + 2,
            Directory.GetFiles(goalDirectory, "*.attempt.json", SearchOption.TopDirectoryOnly).Length);
        Assert.Contains(result.Decisions, decision =>
            decision.Path == goalDirectory &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "non-terminal-evidence-unbounded-by-policy");
        Assert.Contains(
            "nonTerminalUnbounded=1",
            RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AcceptanceRetention_OrdinalBeatsMtime_PreservesFinalAndLastFailure()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        var failingMetadata = fixture.WriteAttempt(goalDirectory, "failure", 1, failed: true, reconciled: true);
        var finalMetadata = fixture.WriteAttempt(goalDirectory, "final", 2, failed: false, reconciled: true);
        var oldMetadata = fixture.WriteAttempt(goalDirectory, "old", 0, failed: false, reconciled: true);
        var failingLog = Path.Combine(goalDirectory, "failure.err.log");
        var finalLog = Path.Combine(goalDirectory, "final.out.log");
        var oldLog = Path.Combine(goalDirectory, "old.out.log");
        File.WriteAllText(failingLog, "failure");
        File.WriteAllText(finalLog, "final");
        File.WriteAllText(oldLog, "old");
        SetAge(failingMetadata, 1);
        SetAge(finalMetadata, 30);
        SetAge(oldMetadata, 30);
        SetAge(failingLog, 30);
        SetAge(finalLog, 30);
        SetAge(oldLog, 30);

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(failingLog));
        Assert.True(File.Exists(finalLog));
        Assert.False(File.Exists(oldLog));
    }

    [Xunit.Fact]
    public void AcceptanceRetention_CorruptAttemptMetadataRetainsTheEntireGoalDirectory()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        var corruptMetadata = Path.Combine(goalDirectory, "corrupt.attempt.json");
        var oldLog = Path.Combine(goalDirectory, "corrupt.out.log");
        File.WriteAllText(corruptMetadata, "{not-json");
        File.WriteAllText(oldLog, "ambiguous evidence");
        SetAge(corruptMetadata, 30);
        SetAge(oldLog, 30);

        var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(corruptMetadata));
        Assert.True(File.Exists(oldLog));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == goalDirectory &&
            decision.Action == EvidenceRetentionAction.RetainedUndecidable &&
            decision.Reason.StartsWith("attempt-metadata-unreadable:", StringComparison.Ordinal));
        var receipt = RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result);
        Assert.Contains("status=partial", receipt, StringComparison.Ordinal);
        Assert.Contains("retainedUndecidable=1", receipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AcceptanceRetention_UnreconciledAttempt_DefersWholeGoal()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "live", 1, failed: false, reconciled: false);
        var log = Path.Combine(goalDirectory, "live.out.log");
        File.WriteAllText(log, "live");
        SetAge(log, 30);

        var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(log));
        Assert.Contains(result.Decisions, decision => decision.Action == EvidenceRetentionAction.DeferredLive);
    }

    [Xunit.Fact]
    public void PreReviewRetention_TerminalGoal_IsCoveredByDecisionPolicy()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "pre-review-evidence-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "final", 2, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "old", 1, failed: false, reconciled: true);
        var oldLog = Path.Combine(goalDirectory, "old.out.log");
        File.WriteAllText(oldLog, "old");
        SetAge(oldLog, 30);

        var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(oldLog));
        Assert.Contains(result.Decisions, decision =>
            decision.Family == EvidenceArtifactFamily.PreReviewEvidenceAttempts &&
            decision.Action == EvidenceRetentionAction.Deleted);
    }

    [Xunit.Fact]
    public void WorkerRetention_ActiveGoalWithSamePrefix_RetainsTerminalEvidence()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteSuccessfulExit(Now.AddDays(-30));
        var activeCollision = new StorageRetentionGoal(
            GoalId[..8] + "aaaaaaaaaaaaaaaaaaaaaaaa",
            GoalStatus.Active,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Running });

        var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed), activeCollision);

        Assert.True(File.Exists(path));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == path && decision.OwnerResolution == EvidenceOwnerResolution.AmbiguousPrefix);
    }

    [Xunit.Fact]
    public void WorkerRetention_UnmatchedTaskPrefix_RetainsEvidence()
    {
        using var fixture = new RetentionFixture();
        var path = Path.Combine(fixture.LogDirectory, $"{GoalId[..8]}-aaaaaaaa-20260801120000.exit.txt");
        DispatchExitArtifacts.Write(path, DispatchExitArtifacts.Native(0, "completed", Now.AddDays(-30)));
        SetAge(path, 30);

        var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(path));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == path && decision.OwnerResolution == EvidenceOwnerResolution.Unmatched);
    }

    [Xunit.Fact]
    public void Prompts_ExactTerminalOwnerDeletesOnlyIndexedAgedFile()
    {
        using var fixture = new RetentionFixture();
        var promptRoot = Path.Combine(fixture.OrchestratorDirectory, "prompts");
        Directory.CreateDirectory(promptRoot);
        var indexed = Path.Combine(promptRoot, "indexed.md");
        var unindexed = Path.Combine(promptRoot, "unindexed.md");
        File.WriteAllText(indexed, "indexed");
        File.WriteAllText(unindexed, "unindexed");
        SetAge(indexed, 30);
        SetAge(unindexed, 30);
        var goal = TerminalGoal(WorkTaskStatus.Completed) with { PromptPaths = [indexed] };

        var result = fixture.Run(goal);

        Assert.False(File.Exists(indexed));
        Assert.True(File.Exists(unindexed));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == unindexed && decision.Action == EvidenceRetentionAction.RetainedUndecidable);
    }

    [Xunit.Fact]
    public void GoalEvents_ReplayContractPreservesTerminalJsonl()
    {
        using var fixture = new RetentionFixture();
        var eventsRoot = Path.Combine(fixture.OrchestratorDirectory, "goal-events");
        Directory.CreateDirectory(eventsRoot);
        var path = Path.Combine(eventsRoot, GoalId + ".jsonl");
        File.WriteAllText(path, "{\"cursor\":1}");
        SetAge(path, 90);

        var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.Equal("{\"cursor\":1}", File.ReadAllText(path));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == path &&
            decision.Family == EvidenceArtifactFamily.GoalEvents &&
            decision.Reason == "replay-contract-requires-raw-jsonl");
    }

    [Xunit.Fact]
    public async Task Sweep_WhenLeaseIsHeld_DefersWithoutDeleting()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteSuccessfulExit(Now.AddDays(-30));
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, StorageRetentionMaintenance.LeaseNameFor(fixture.OrchestratorDirectory));
            mutex.WaitOne();
            acquired.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Retention lease test release signal was not observed.");
            }
            mutex.ReleaseMutex();
        });

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(30)), "Retention lease holder did not acquire the mutex.");
        try
        {
            var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

            Assert.True(File.Exists(path));
            var decision = Assert.Single(result.Decisions);
            Assert.Equal(EvidenceRetentionAction.DeferredLease, decision.Action);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Xunit.Fact]
    public async Task AcceptanceRetention_WhenAttemptWriterLeaseIsHeld_DefersGoalWithoutDeleting()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "final", 2, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "old", 1, failed: false, reconciled: true);
        var oldLog = Path.Combine(goalDirectory, "old.out.log");
        File.WriteAllText(oldLog, "old");
        SetAge(oldLog, 30);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, StorageRetentionMaintenance.AttemptLeaseNameFor(goalDirectory));
            mutex.WaitOne();
            acquired.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Acceptance writer lease test release signal was not observed.");
            }
            mutex.ReleaseMutex();
        });

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(30)), "Acceptance writer lease holder did not acquire the mutex.");
        try
        {
            var result = fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

            Assert.True(File.Exists(oldLog));
            Assert.Contains(result.Decisions, decision =>
                decision.Family == EvidenceArtifactFamily.AcceptanceGateAttempts &&
                decision.Action == EvidenceRetentionAction.DeferredLease &&
                decision.Reason == "attempt-writer-lease-unavailable");
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Xunit.Fact]
    public void Sweep_WhenLaterFamilyThrows_ReturnsPartialFailedDecisionLedger()
    {
        using var fixture = new RetentionFixture();
        var deletedPath = fixture.WriteSuccessfulExit(Now.AddDays(-30));

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [TerminalGoal(WorkTaskStatus.Completed)],
            Now,
            beforeGoalJournalArchiveForTests: () => throw new IOException("injected archive failure"));

        Assert.False(File.Exists(deletedPath));
        Assert.True(result.Failed);
        Assert.Contains(result.Decisions, decision =>
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.BytesReclaimed > 0);
        Assert.Contains(result.Decisions, decision =>
            decision.Action == EvidenceRetentionAction.Failed &&
            decision.Reason == "sweep-failed" &&
            !string.IsNullOrWhiteSpace(decision.FailureExceptionType));
        Assert.Contains("status=partial-failed", RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MtpUnattributedRetention_ReclaimsAged_KeepsLiveAndYoung()
    {
        using var fixture = new RetentionFixture();
        var aged = fixture.WriteUnattributedMtpRun("summary-aged", Now.AddHours(-50));
        var live = fixture.WriteUnattributedMtpRun(
            "summary-live",
            Now.AddHours(-50),
            ownerProcessId: Environment.ProcessId);
        var young = fixture.WriteUnattributedMtpRun("summary-young", Now.AddMinutes(-5));

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot);

        Assert.False(Directory.Exists(aged));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(young));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == aged &&
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "mtp-unattributed-past-age-bound");
        Assert.Contains(result.Decisions, decision =>
            decision.Path == live &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "mtp-unattributed-owner-is-live");
        Assert.Contains(result.Decisions, decision =>
            decision.Path == young &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "mtp-unattributed-is-young");
        var reclaimedBytes = Assert.Single(result.Decisions, decision => decision.Path == aged).BytesReclaimed;
        Assert.True(reclaimedBytes > 0);
        var receipt = RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result);
        Assert.Contains("mtpUnattributedExamined=3", receipt, StringComparison.Ordinal);
        Assert.Contains("mtpUnattributedReclaimed=1", receipt, StringComparison.Ordinal);
        Assert.Contains("mtpUnattributedRetainedLive=1", receipt, StringComparison.Ordinal);
        Assert.Contains("mtpUnattributedRetainedYoung=1", receipt, StringComparison.Ordinal);
        Assert.Contains($"mtpUnattributedBytesReclaimed={reclaimedBytes}", receipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MtpUnattributedRetention_InaccessibleOwnerIsPreservedAndSweepContinues()
    {
        using var fixture = new RetentionFixture();
        var laterFamilyReached = false;
        var inaccessibleOwner = fixture.WriteUnattributedMtpRun(
            "summary-inaccessible-owner",
            Now.AddHours(-50),
            ownerProcessId: 1234);

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            beforeGoalJournalArchiveForTests: () => laterFamilyReached = true,
            mtpResultsRoot: fixture.MtpResultsRoot,
            mtpProcessHasExitedForTests: _ =>
                throw new System.ComponentModel.Win32Exception(5, "Access is denied."));

        Assert.True(Directory.Exists(inaccessibleOwner));
        Assert.True(laterFamilyReached);
        Assert.False(result.Failed, "An inaccessible live owner must not abort the retention sweep.");
        Assert.Contains(result.Decisions, decision =>
            decision.Path == inaccessibleOwner &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "mtp-unattributed-owner-is-live");
    }

    [Xunit.Fact]
    public void MtpUnattributedRetention_DeadOwnerIsEligibleForReclamation()
    {
        using var fixture = new RetentionFixture();
        var deadOwner = fixture.WriteUnattributedMtpRun(
            "summary-dead-owner",
            Now.AddHours(-50),
            ownerProcessId: 1234);

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot,
            mtpProcessHasExitedForTests: _ => true);

        Assert.False(Directory.Exists(deadOwner));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == deadOwner &&
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "mtp-unattributed-past-age-bound");
    }

    [Xunit.Fact]
    public void MtpUnattributedRetention_SizeRuleReclaimsOldestFirstUntilWithinBudget()
    {
        using var fixture = new RetentionFixture();
        var oldest = fixture.WriteUnattributedMtpRun("summary-oldest", Now.AddHours(-3), payloadBytes: 128);
        var middle = fixture.WriteUnattributedMtpRun("summary-middle", Now.AddHours(-2), payloadBytes: 128);
        var newest = fixture.WriteUnattributedMtpRun("summary-newest", Now.AddHours(-1), payloadBytes: 128);
        var maxBytes = DirectoryBytes(newest);

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot,
            mtpUnattributedMaxBytesForTests: maxBytes);

        Assert.False(Directory.Exists(oldest));
        Assert.False(Directory.Exists(middle));
        Assert.True(Directory.Exists(newest));
        Assert.Equal(
            [oldest, middle],
            result.Decisions
                .Where(decision =>
                    decision.Action == EvidenceRetentionAction.Deleted &&
                    decision.Reason == "mtp-unattributed-past-size-bound")
                .Select(decision => decision.Path)
                .ToArray());
    }

    [Xunit.Fact]
    public void MtpUnattributedRetention_PerSweepBoundDrainsAcrossSweeps()
    {
        const int reclaimBound = 2;
        using var fixture = new RetentionFixture();
        for (var ordinal = 0; ordinal < reclaimBound + 3; ordinal++)
        {
            fixture.WriteUnattributedMtpRun(
                $"summary-aged-{ordinal}",
                Now.AddHours(-60 + ordinal));
        }

        var first = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot,
            mtpUnattributedMaxBytesForTests: long.MaxValue,
            mtpUnattributedReclaimsPerSweepForTests: reclaimBound);

        Assert.Equal(3, Directory.GetDirectories(fixture.MtpResultsRoot).Length);
        Assert.Equal(reclaimBound, first.Decisions.Count(decision =>
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.AttemptId == "unowned"));
        Assert.Contains(first.Decisions, decision =>
            decision.Reason == "mtp-unattributed-deferred-to-next-sweep");

        var second = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot,
            mtpUnattributedMaxBytesForTests: long.MaxValue,
            mtpUnattributedReclaimsPerSweepForTests: reclaimBound);

        Assert.Single(Directory.GetDirectories(fixture.MtpResultsRoot));
        Assert.Equal(reclaimBound, second.Decisions.Count(decision =>
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.AttemptId == "unowned"));
    }

    [Xunit.Fact]
    public void MtpUnattributedRetention_SkipsProcessRootsAndLeaseFiles()
    {
        using var fixture = new RetentionFixture();
        var processRoot = Path.Combine(fixture.MtpResultsRoot, "p1a2b");
        Directory.CreateDirectory(processRoot);
        var leaseFile = Path.Combine(fixture.MtpResultsRoot, ".p1a2b.owner.lock");
        File.WriteAllText(leaseFile, "lease");

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot,
            mtpUnattributedMaxBytesForTests: 0);

        Assert.True(Directory.Exists(processRoot));
        Assert.True(File.Exists(leaseFile));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == processRoot &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "mtp-process-temp-root-owned-by-reaper");
        Assert.DoesNotContain(result.Decisions, decision =>
            decision.Path == processRoot &&
            decision.Action == EvidenceRetentionAction.Deleted);
    }

    [Xunit.Fact]
    public async Task MtpRetention_TerminalOwnedAgedRun_IsDeletedByScheduledPolicy()
    {
        using var fixture = new RetentionFixture();
        using var localAppData = new EnvironmentVariableScope("LOCALAPPDATA", fixture.LocalApplicationDataDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var goalDirectory = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "mtp-old", ordinal: 1, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "mtp-final", ordinal: 2, failed: false, reconciled: true);
        var runDirectory = fixture.WriteMtpRun("mtp-old", Now.AddDays(-30));
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveGoalSnapshotsAsync([
            GoalSnapshotFor(GoalId, GoalStatus.Completed, WorkTaskStatus.Completed)
        ]);

        var cadence = RunEventMaintenanceCadence.TryRunIfDue(
            Path.Combine(fixture.ExecutionDirectory, "run-events-mtp-terminal.db"),
            Path.Combine(fixture.LogDirectory, "mtp-terminal-conduct.jsonl"),
            () => Now,
            workspace: workspace,
            mtpResultsRootOverride: fixture.MtpResultsRoot);
        var result = Assert.IsType<StorageRetentionResult>(cadence.ArtifactRetention);

        Assert.False(Directory.Exists(runDirectory));
        Assert.Contains(result.Decisions, decision =>
            decision.Family == EvidenceArtifactFamily.MtpTestRuns &&
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.AttemptId == "mtp-old");
    }

    [Xunit.Fact]
    public void MtpRetention_FutureDatedRunRetainsItsCountBoundOwnerMetadata()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        var ownerMetadata = fixture.WriteAttempt(
            goalDirectory,
            "mtp-future",
            ordinal: 1,
            failed: false,
            reconciled: true);
        for (var ordinal = 2;
             ordinal <= ConductorParallelAcceptanceAttemptCoordinator.RetainedAttemptCountPerGoal + 2;
             ordinal++)
        {
            fixture.WriteAttempt(goalDirectory, $"attempt-{ordinal:D2}", ordinal, failed: false, reconciled: true);
        }
        var runDirectory = fixture.WriteMtpRun("mtp-future", Now.AddMinutes(6));

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [TerminalGoal(WorkTaskStatus.Completed)],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot);

        Assert.True(Directory.Exists(runDirectory));
        Assert.True(File.Exists(ownerMetadata));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == ownerMetadata &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "retained-test-artifact-owner-metadata");
    }

    [Xunit.Fact]
    public void MtpRetention_AmbiguousRunRetainsEveryCandidateOwnerMetadata()
    {
        const string secondGoalId = "33333333333333333333333333333333";
        using var fixture = new RetentionFixture();
        var firstDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        var secondDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", secondGoalId);
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstMetadata = fixture.WriteAttempt(
            firstDirectory,
            "mtp-ambiguous",
            ordinal: 1,
            failed: false,
            reconciled: true);
        var secondMetadata = fixture.WriteAttempt(
            secondDirectory,
            "mtp-ambiguous",
            ordinal: 1,
            failed: false,
            reconciled: true,
            goalId: secondGoalId);
        fixture.WriteAttempt(firstDirectory, "first-final", ordinal: 2, failed: false, reconciled: true);
        fixture.WriteAttempt(
            secondDirectory,
            "second-final",
            ordinal: 2,
            failed: false,
            reconciled: true,
            goalId: secondGoalId);
        SetAge(firstMetadata, 30);
        SetAge(secondMetadata, 30);
        var runDirectory = fixture.WriteMtpRun("mtp-ambiguous", Now.AddDays(-30));
        var secondGoal = new StorageRetentionGoal(
            secondGoalId,
            GoalStatus.Completed,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Completed });

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [TerminalGoal(WorkTaskStatus.Completed), secondGoal],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot);

        Assert.True(Directory.Exists(runDirectory));
        Assert.True(File.Exists(firstMetadata));
        Assert.True(File.Exists(secondMetadata));
        Assert.Equal(2, result.Decisions.Count(decision =>
            (decision.Path == firstMetadata || decision.Path == secondMetadata) &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "retained-test-artifact-owner-metadata"));
    }

    [Xunit.Fact]
    public void MtpRetention_RevalidatesProtectedAttemptAfterCandidateSnapshotBeforeDeletion()
    {
        using var fixture = new RetentionFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var goalDirectory = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "mtp-old", ordinal: 1, failed: false, reconciled: true);
        fixture.WriteAttempt(goalDirectory, "mtp-final", ordinal: 2, failed: false, reconciled: true);
        var runDirectory = fixture.WriteMtpRun("mtp-old", Now.AddDays(-30));

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [TerminalGoal(WorkTaskStatus.Completed)],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot,
            beforeMtpCandidateDeletionForTests: _ =>
            {
                using var writerLease = StorageRetentionMaintenance.AcquireAttemptWriterLease(goalDirectory);
                fixture.WriteAttempt(goalDirectory, "mtp-old", ordinal: 3, failed: false, reconciled: true);
            });

        Assert.True(Directory.Exists(runDirectory));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == runDirectory &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "final-attempt" &&
            decision.AttemptOrdinal == 3);
    }

    [Xunit.Fact]
    public async Task MtpRetention_NonTerminalOwner_IsPreservedWithDecision()
    {
        using var fixture = new RetentionFixture();
        using var localAppData = new EnvironmentVariableScope("LOCALAPPDATA", fixture.LocalApplicationDataDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var goalDirectory = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        fixture.WriteAttempt(goalDirectory, "mtp-active", ordinal: 1, failed: false, reconciled: true);
        var runDirectory = fixture.WriteMtpRun("mtp-active", Now.AddDays(-30));
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveGoalSnapshotsAsync([
            GoalSnapshotFor(GoalId, GoalStatus.Active, WorkTaskStatus.Running)
        ]);

        var cadence = RunEventMaintenanceCadence.TryRunIfDue(
            Path.Combine(fixture.ExecutionDirectory, "run-events-mtp-active.db"),
            Path.Combine(fixture.LogDirectory, "mtp-active-conduct.jsonl"),
            () => Now,
            workspace: workspace,
            mtpResultsRootOverride: fixture.MtpResultsRoot);
        var result = Assert.IsType<StorageRetentionResult>(cadence.ArtifactRetention);

        Assert.True(Directory.Exists(runDirectory));
        Assert.Contains(result.Decisions, decision =>
            decision.Family == EvidenceArtifactFamily.MtpTestRuns &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.OwnerResolution == EvidenceOwnerResolution.NonTerminal &&
            decision.AttemptId == "mtp-active");
    }

    [Xunit.Fact]
    public void MtpRetention_LegacyDirectoryReportsBytesAsUnmeasuredWithoutRecursiveWalk()
    {
        using var fixture = new RetentionFixture();
        var legacyDirectory = Path.Combine(fixture.MtpResultsRoot, "legacy-without-sidecar");
        Directory.CreateDirectory(legacyDirectory);
        var payload = Path.Combine(legacyDirectory, "legacy.trx");
        File.WriteAllText(payload, new string('x', 123));

        var result = StorageRetentionMaintenance.Run(
            fixture.LogDirectory,
            fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory,
            [],
            Now,
            mtpResultsRoot: fixture.MtpResultsRoot);
        var decision = Assert.Single(result.Decisions, item =>
            item.Path == legacyDirectory &&
            item.Action == EvidenceRetentionAction.RetainedUndecidable);

        Assert.Equal(0, decision.BytesAttempted);
        Assert.DoesNotContain(
            "mtpUnreclaimableBytes=",
            RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result),
            StringComparison.Ordinal);
        Assert.Contains(
            "mtpUnreclaimableDirectoriesUnmeasured=1",
            RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MtpRetention_YoungTerminalRuns_AreBoundedByCount()
    {
        using var fixture = new RetentionFixture();
        using var localAppData = new EnvironmentVariableScope("LOCALAPPDATA", fixture.LocalApplicationDataDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var goalDirectory = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        string? retainedMtpOwnerMetadata = null;
        for (var ordinal = 1; ordinal <= StorageRetentionMaintenance.MtpResultMaxRetainedDirectories + 2; ordinal++)
        {
            var attemptId = $"mtp-{ordinal:D3}";
            var metadata = fixture.WriteAttempt(goalDirectory, attemptId, ordinal, failed: false, reconciled: true);
            if (ordinal == 1)
            {
                retainedMtpOwnerMetadata = metadata;
            }
            if (ordinal <= StorageRetentionMaintenance.MtpResultMaxRetainedDirectories + 1)
            {
                fixture.WriteMtpRun(attemptId, Now.AddMinutes(-ordinal));
            }
        }
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            new GoalId(GoalId),
            "bounded MTP retention fixture",
            [new TaskSpec(new TaskId(TaskId), "fixture task", AgentRole.Developer)]);
        goal = kernel.CancelGoal(goal.Id, "fixture terminal state");

        var result = StorageRetentionMaintenance.Run(workspace, [goal], Now);

        var root = Path.Combine(fixture.LocalApplicationDataDirectory, "Temp", "Low", "mcg-tests");
        Assert.Contains(result.Decisions, decision =>
            decision.Family == EvidenceArtifactFamily.MtpTestRuns &&
            decision.Action == EvidenceRetentionAction.Deleted &&
            decision.Reason == "past-count-bound");
        Assert.Equal(
            StorageRetentionMaintenance.MtpResultMaxRetainedDirectories,
            Directory.GetDirectories(root, "retained-*", SearchOption.TopDirectoryOnly).Length);
        Assert.NotNull(retainedMtpOwnerMetadata);
        Assert.True(File.Exists(retainedMtpOwnerMetadata));
        Assert.Contains(result.Decisions, decision =>
            decision.Path == retainedMtpOwnerMetadata &&
            decision.Action == EvidenceRetentionAction.Preserved &&
            decision.Reason == "retained-test-artifact-owner-metadata");
    }

    [Xunit.Fact]
    public async Task ReceiptAppendFailure_PreservesDestructiveLedgerAndRequiredJournalReceipt()
    {
        using var fixture = new RetentionFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveGoalSnapshotsAsync([
            GoalSnapshotFor(GoalId, GoalStatus.Completed, WorkTaskStatus.Completed)
        ]);
        var deletedPath = fixture.WriteSuccessfulExit(Now.AddDays(-30));
        var runEventStorePath = Path.Combine(fixture.ExecutionDirectory, "run-events-receipt-failure.db");
        var conductLogPath = Path.Combine(fixture.LogDirectory, "receipt-failure-conduct.jsonl");

        var result = RunEventMaintenanceCadence.TryRunIfDue(
            runEventStorePath,
            conductLogPath,
            () => Now,
            maintenanceOperation: (_, _) =>
            {
                File.Delete(runEventStorePath);
                Directory.CreateDirectory(runEventStorePath);
                return new RunEventMaintenanceResult(
                    Deferred: false,
                    DeferredReason: null,
                    ConductorTickRowsDeleted: 0,
                    AgedConductorTickRowsDeleted: 0,
                    OversizedConductorTickRowsDeleted: 0,
                    DeletedPayloadBytesEstimate: 0,
                    MaxRowsDeletedInTransaction: 0,
                    Duration: TimeSpan.Zero,
                    BytesBefore: 0,
                    BytesAfter: 0,
                    VacuumRequested: false,
                    VacuumCompleted: false,
                    VacuumDeferred: false);
            },
            workspace: workspace,
            mtpResultsRootOverride: fixture.MtpResultsRoot);

        Assert.False(File.Exists(deletedPath));
        var retention = Assert.IsType<StorageRetentionResult>(result.ArtifactRetention);
        Assert.True(result.Failed);
        Assert.True(retention.Failed);
        Assert.Contains(retention.Decisions, decision =>
            decision.Action == EvidenceRetentionAction.Deleted && decision.Path == deletedPath);
        Assert.Contains(retention.Decisions, decision =>
            decision.Action == EvidenceRetentionAction.Failed &&
            decision.Reason == "receipt-persistence-failed");
        var conductLog = File.ReadAllText(conductLogPath);
        Assert.Contains("storage-retention-sweep", conductLog, StringComparison.Ordinal);
        Assert.Contains("policyVersion=", conductLog, StringComparison.Ordinal);
        Assert.Contains("status=partial-failed", conductLog, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalOperationJournal_archive_is_skipped_by_ReadAll")]
    public void ArchiveIsSkippedByReadAll()
    {
        using var fixture = new RetentionFixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("retired retention fixture", [
            new TaskSpec(new TaskId(TaskId), "fixture task", AgentRole.Developer)
        ]);
        var source = GoalOperationJournal.PathFor(fixture.ExecutionDirectory, goal.Id);
        GoalOperationJournal.RecordTerminalDisposition(
            fixture.ExecutionDirectory,
            goal,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "retired retention fixture"));

        fixture.Run(new StorageRetentionGoal(
            goal.Id.Value,
            GoalStatus.Completed,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Completed }));

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(GoalOperationJournal.ArchivePathFor(fixture.ExecutionDirectory, goal.Id)));
        Assert.DoesNotContain(goal.Id, GoalOperationJournal.ReadAll(fixture.ExecutionDirectory).Keys);
        Assert.False(GoalOperationJournal.ReadActive(fixture.ExecutionDirectory, goal.Id).HasEntries);
        Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(
            GoalOperationJournal.Read(fixture.ExecutionDirectory, goal.Id)));
    }

    [Xunit.Fact(DisplayName = "GoalOperationJournal_terminal_goal_without_retired_disposition_is_not_archived")]
    public void TerminalGoalWithoutRetiredDispositionIsNotArchived()
    {
        using var fixture = new RetentionFixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("landed retention fixture", [
            new TaskSpec(new TaskId(TaskId), "fixture task", AgentRole.Developer)
        ]);
        GoalOperationJournal.RecordTerminalDisposition(
            fixture.ExecutionDirectory,
            goal,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Landed, "landed retention fixture"));

        fixture.Run(new StorageRetentionGoal(
            goal.Id.Value,
            GoalStatus.Completed,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Completed }));

        Assert.True(File.Exists(GoalOperationJournal.PathFor(fixture.ExecutionDirectory, goal.Id)));
        Assert.False(File.Exists(GoalOperationJournal.ArchivePathFor(fixture.ExecutionDirectory, goal.Id)));
    }

    [Xunit.Fact(DisplayName = "RollingLogWriteStream_crossing_byte_threshold_rolls_without_truncating")]
    public void CrossingByteThresholdRollsWithoutTruncating()
    {
        using var fixture = new RetentionFixture();
        var path = Path.Combine(fixture.LogDirectory, "rolling.out.log");
        using (var stream = new RollingLogWriteStream(path, FileMode.Create, maxBytes: 8))
        {
            stream.Write("12345678"u8);
            stream.Write("90"u8);
        }

        Assert.Equal("12345678", File.ReadAllText(path + ".part-0001"));
        Assert.Equal("90", File.ReadAllText(path));
    }

    private static StorageRetentionGoal TerminalGoal(WorkTaskStatus taskStatus) =>
        new(GoalId, GoalStatus.Completed, new Dictionary<string, WorkTaskStatus> { [TaskId] = taskStatus });

    private static GoalSnapshot GoalSnapshotFor(
        string goalId,
        GoalStatus goalStatus,
        WorkTaskStatus taskStatus) =>
        new(
            goalId,
            "retention fixture",
            goalStatus,
            [new TaskSnapshot(
                TaskId,
                "retention task",
                AgentRole.Developer,
                taskStatus,
                AssignedAgentId: null,
                LastExecution: null,
                LastVerification: null,
                VerificationHistory: null,
                LastDispatch: null,
                LastProcess: null)],
            []);

    private static string SuccessfulTrx(string testName) =>
        $"<TestRun><Results><UnitTestResult testName=\"{testName}\" outcome=\"Passed\" /></Results>" +
        "<ResultSummary><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>";

    private static string FailedTrx(string testName) =>
        $"<TestRun><Results><UnitTestResult testName=\"{testName}\" outcome=\"Failed\"><Output><ErrorInfo>" +
        $"<Message>Synthetic failure: {testName}</Message><StackTrace>at {testName}()&#10;at Runner.Invoke()</StackTrace>" +
        "</ErrorInfo></Output></UnitTestResult></Results>" +
        "<ResultSummary><Counters total=\"1\" executed=\"1\" passed=\"0\" failed=\"1\" /></ResultSummary></TestRun>";

    private static void SetAge(string path, int days) => File.SetLastWriteTimeUtc(path, Now.AddDays(-days).UtcDateTime);

    private static long DirectoryBytes(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

    private sealed class RetentionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-retention-tests", Guid.NewGuid().ToString("n"));

        public RetentionFixture()
        {
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(OrchestratorDirectory);
        }

        public string ExecutionDirectory => _root;
        public string LocalApplicationDataDirectory => Path.Combine(_root, "LocalAppData");
        public string MtpResultsRoot => Path.Combine(LocalApplicationDataDirectory, "Temp", "Low", "mcg-tests");
        public string OrchestratorDirectory => Path.Combine(_root, ".orchestrator");
        public string LogDirectory => Path.Combine(OrchestratorDirectory, "logs");

        public string WorkerArtifactPath(string suffix) =>
            Path.Combine(LogDirectory, $"{GoalId[..8]}-{TaskId[..8]}-20260801120000.{suffix}");

        public string WriteWorkerArtifact(string suffix, DateTimeOffset lastWrite)
        {
            var path = WorkerArtifactPath(suffix);
            File.WriteAllText(path, "artifact");
            File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
            return path;
        }

        public string WriteSuccessfulExit(DateTimeOffset lastWrite)
        {
            var path = WorkerArtifactPath("exit.txt");
            DispatchExitArtifacts.Write(path, DispatchExitArtifacts.Native(0, "completed", lastWrite));
            File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
            return path;
        }

        public string WriteSuccessfulChildExit(DateTimeOffset lastWrite)
        {
            var path = WorkerArtifactPath("child-exit.json");
            File.WriteAllText(path, "{\"exitCode\":0}");
            File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
            return path;
        }

        public string WriteAttempt(
            string goalDirectory,
            string attemptId,
            int ordinal,
            bool failed,
            bool reconciled,
            string? goalId = null)
        {
            var path = Path.Combine(goalDirectory, attemptId + ".attempt.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                attemptId,
                goalId = goalId ?? GoalId,
                ordinal,
                startedAt = Now.AddMinutes(ordinal),
                outcome = failed ? 2 : 1,
                reconciledAt = reconciled ? Now : (DateTimeOffset?)null
            }));
            return path;
        }

        public string WriteMtpRun(string attemptId, DateTimeOffset createdAt)
        {
            var root = Path.Combine(LocalApplicationDataDirectory, "Temp", "Low", "mcg-tests");
            var directory = Path.Combine(root, $"retained-{attemptId}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, ".mtp-run-ownership.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    attemptId,
                    machineName = Environment.MachineName,
                    ownerProcessId = 0,
                    createdAt,
                    runLabel = "retention-fixture"
                }));
            File.WriteAllText(Path.Combine(directory, "failure.trx"), "retained failure evidence");
            return directory;
        }

        public string WriteUnattributedMtpRun(
            string name,
            DateTimeOffset createdAt,
            int? ownerProcessId = null,
            int payloadBytes = 0)
        {
            var directory = Path.Combine(MtpResultsRoot, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, ".mtp-run-ownership.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    attemptId = "unowned",
                    machineName = Environment.MachineName,
                    ownerProcessId,
                    createdAt,
                    runLabel = "retention-fixture"
                }));
            if (payloadBytes > 0)
            {
                File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[payloadBytes]);
            }
            return directory;
        }

        public StorageRetentionResult Run(params StorageRetentionGoal[] goals) =>
            StorageRetentionMaintenance.Run(LogDirectory, OrchestratorDirectory, ExecutionDirectory, goals, Now);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }


    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
