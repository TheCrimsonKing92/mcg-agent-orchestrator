using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RetryAdmissionReservationStoreTests
{
    [Xunit.Fact]
    public async Task MissingDurableGoalCannotAuthorizePaidWorkerStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);

        var claim = await RetryAdmissionReservationStore.TryClaimStartAsync(
            databasePath,
            GoalId.New(),
            TaskId.New(),
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            "missing-owner",
            DateTimeOffset.Parse("2026-08-25T12:01:00Z"));

        Assert.False(claim ?? true);
    }

    [Xunit.Fact]
    public async Task ConcurrentContendersCreateOneAllowedReservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal("Atomic retry admission", [task]);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "A new source finding requires repair.",
            retryCause: RetryCause.NewSourceFinding);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(kernel);
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value,
            task.Id.Value,
            task.RequiredRole,
            "OpenAI",
            "gpt",
            PaidRouteClassification.Paid,
            "candidate",
            "criteria",
            [],
            [],
            [],
            [],
            "base",
            "main"));
        var at = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var preparedDispatch = new TaskDispatchRecord(
            "worker", "command", "worktree", at,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);

        var initialSnapshot = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        var permissiveResults = new[]
        {
            RetryAdmissionSnapshotReservation.Apply(
                initialSnapshot, task.Id, fingerprint, PaidRouteClassification.Paid,
                RetryCause.NewSourceFinding, preparedDispatch, at, "owner-a", at.AddMinutes(1)),
            RetryAdmissionSnapshotReservation.Apply(
                initialSnapshot, task.Id, fingerprint, PaidRouteClassification.Paid,
                RetryCause.NewSourceFinding, preparedDispatch, at.AddTicks(1), "owner-b", at.AddMinutes(1))
        };

        Assert.Equal(2, permissiveResults.Count(result => result.Decision == RetryAdmissionDecision.Allowed));

        var results = await Task.WhenAll(
            RetryAdmissionReservationStore.TryReserveAsync(
                databasePath, goal.Id, task.Id, fingerprint,
                PaidRouteClassification.Paid, RetryCause.NewSourceFinding, preparedDispatch, at,
                "owner-a", at.AddMinutes(1)),
            RetryAdmissionReservationStore.TryReserveAsync(
                databasePath, goal.Id, task.Id, fingerprint,
                PaidRouteClassification.Paid, RetryCause.NewSourceFinding, preparedDispatch, at.AddTicks(1),
                "owner-b", at.AddMinutes(1)));

        Assert.Single(results, result => result?.Decision == RetryAdmissionDecision.Allowed);
        Assert.Single(results, result => result?.Decision == RetryAdmissionDecision.Prevented);
        var saved = await repository.LoadGoalAsync(goal.Id);
        Assert.Equal(2, saved!.Tasks.Single().RetryAdmissionHistory!.Count);
    }

    [Xunit.Fact]
    public async Task ConcurrentDifferentAttemptsKeepWinningPreparedDispatchBoundToAllowedReservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fence concurrent prepared attempts", [task]);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "A new source finding requires repair.",
            retryCause: RetryCause.NewSourceFinding);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(kernel);
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value, task.Id.Value, task.RequiredRole, "OpenAI", "gpt",
            PaidRouteClassification.Paid, "candidate", "criteria", [], [], [], [], "base", "main"));
        var firstAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var secondAt = firstAt.AddSeconds(1);
        var firstDispatch = new TaskDispatchRecord(
            "worker", "command-a", "worktree", firstAt,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        var secondDispatch = new TaskDispatchRecord(
            "worker", "command-b", "worktree", secondAt,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);

        var results = await Task.WhenAll(
            RetryAdmissionReservationStore.TryReserveAsync(
                databasePath, goal.Id, task.Id, fingerprint,
                PaidRouteClassification.Paid, RetryCause.NewSourceFinding, firstDispatch, firstAt,
                "owner-a", firstAt.AddMinutes(1)),
            RetryAdmissionReservationStore.TryReserveAsync(
                databasePath, goal.Id, task.Id, fingerprint,
                PaidRouteClassification.Paid, RetryCause.NewSourceFinding, secondDispatch, secondAt,
                "owner-b", secondAt.AddMinutes(1)));

        var allowed = Assert.Single(results, result => result?.Decision == RetryAdmissionDecision.Allowed)!;
        var prevented = Assert.Single(results, result => result?.Decision == RetryAdmissionDecision.Prevented)!;
        var saved = await repository.LoadGoalAsync(goal.Id);
        var savedTask = Assert.Single(saved!.Tasks);
        Assert.Equal(allowed.Receipt.LinkedDispatchAt, savedTask.LastDispatch!.DispatchedAt);
        Assert.NotEqual(prevented.Receipt.LinkedDispatchAt, savedTask.LastDispatch.DispatchedAt);
        var claimed = await RetryAdmissionReservationStore.TryClaimStartAsync(
            databasePath,
            goal.Id,
            task.Id,
            allowed.Receipt.LinkedDispatchAt,
            allowed.Receipt.ReservationOwnerId!,
            secondAt.AddSeconds(1));
        Assert.True(claimed);
    }

    [Xunit.Fact]
    public async Task PreventedRouteAndRecoveryRequestPersistInReservationTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry provider interruption", AgentRole.Developer);
        var goal = kernel.CreateGoal("Persist prevented retry routing atomically", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, task.Id, "Provider interrupted the paid attempt.", RetryCause.ProviderInterruption);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(kernel);
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value, task.Id.Value, task.RequiredRole, "OpenAI", "gpt",
            PaidRouteClassification.Paid, "candidate", "criteria", [], [], [], [], "base", "main"));
        var firstAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var firstDispatch = new TaskDispatchRecord(
            "worker", "command-a", "worktree", firstAt,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        var first = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid,
            RetryCause.ProviderInterruption, firstDispatch, firstAt, "owner-a", firstAt.AddMinutes(1));
        Assert.Equal(RetryAdmissionDecision.Allowed, first!.Decision);
        Assert.True(await RetryAdmissionReservationStore.TryClaimStartAsync(
            databasePath, goal.Id, task.Id, firstAt, "owner-a", firstAt.AddSeconds(1)));
        Assert.True((await RetryAdmissionReservationStore.TryConfirmStartAsync(
            databasePath, goal.Id, task.Id, firstAt, "owner-a", firstAt.AddSeconds(1)))!.Claimed);

        kernel = await repository.LoadAsync();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "worker", "worktree", 1, "", "provider interrupted", firstAt.AddSeconds(2),
                DispatchStartedAt: firstAt.AddSeconds(1)));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Provider interrupted the paid attempt.");
        await repository.SaveAsync(kernel);
        var secondAt = firstAt.AddMinutes(2);
        kernel.RetryTask(
            goal.Id, task.Id, "Provider interrupted the paid attempt.", RetryCause.ProviderInterruption);
        var inMemoryTask = kernel.GetTask(goal.Id, task.Id);
        var inMemoryRetryAt = inMemoryTask.LatestRetryAt;
        var secondDispatch = new TaskDispatchRecord(
            "worker", "command-b", "worktree", secondAt,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);

        var prevented = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid,
            RetryCause.ProviderInterruption, secondDispatch, secondAt, "owner-b", secondAt.AddMinutes(1),
            retryMarkerAt: inMemoryRetryAt,
            retryRoundKind: inMemoryTask.PendingRetryRoundKind,
            retryMessage: "Provider interrupted the paid attempt.");

        Assert.Equal(RetryAdmissionDecision.Prevented, prevented!.Decision);
        var persisted = await repository.LoadAsync();
        var persistedGoal = persisted.GetGoal(goal.Id);
        var persistedTask = persistedGoal.Tasks.Single(candidate => candidate.Id == task.Id);
        Assert.Equal(inMemoryRetryAt, persistedTask.LatestRetryAt);
        Assert.Equal(RetryCause.ProviderInterruption, persistedTask.PendingRetryCause);
        Assert.Equal(RetryAdmissionRoute.EnvironmentalHold, persistedTask.RetryAdmissionHoldRoute);
        Assert.Contains(
            persistedGoal.Timeline,
            item => item.Kind == ProgressKind.NoProgressRedispatchPrevented && item.TaskId == task.Id);
        Assert.Single(
            persisted.HumanInputRequests,
            request => request.GoalId == goal.Id && request.TaskId == task.Id && !request.IsCompleted);
    }

    [Xunit.Fact]
    public async Task StartedReservationCannotBeRecoveredByAnotherOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal("Do not recover a started retry", [task]);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "A new source finding requires repair.",
            retryCause: RetryCause.NewSourceFinding);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(kernel);
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value, task.Id.Value, task.RequiredRole, "OpenAI", "gpt",
            PaidRouteClassification.Paid, "candidate", "criteria", [], [], [], [], "base", "main"));
        var at = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var dispatch = new TaskDispatchRecord(
            "worker", "command", "worktree", at,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        _ = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid,
            RetryCause.NewSourceFinding, dispatch, at, "owner-a", at.AddMinutes(1));
        Assert.True(await RetryAdmissionReservationStore.TryClaimStartAsync(
            databasePath, goal.Id, task.Id, at, "owner-a", at.AddSeconds(1)));
        var confirmation = await RetryAdmissionReservationStore.TryConfirmStartAsync(
            databasePath, goal.Id, task.Id, at, "owner-a", at.AddSeconds(2));
        Assert.True(confirmation!.Claimed);

        var recovery = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid,
            RetryCause.NewSourceFinding, dispatch, at.AddMinutes(2), "owner-b", at.AddMinutes(3),
            reservationRecoveryConfirmed: true);

        Assert.Equal(RetryAdmissionDecision.Prevented, recovery!.Decision);
        Assert.False(await RetryAdmissionReservationStore.TryClaimStartAsync(
            databasePath, goal.Id, task.Id, at, "owner-b", at.AddMinutes(2)));
    }

    [Xunit.Fact]
    public async Task StartClaimIsNotStartedUntilDurableConfirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal("Two phase worker start", [task]);
        kernel.RetryTask(goal.Id, task.Id, "Retry source repair.", RetryCause.NewSourceFinding);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(kernel);
        var at = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value, task.Id.Value, task.RequiredRole, "OpenAI", "gpt",
            PaidRouteClassification.Paid, "candidate", "criteria", [], [], [], [], "base", "main"));
        var dispatch = new TaskDispatchRecord(
            "worker", "command", "worktree", at,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        var reservation = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid,
            RetryCause.NewSourceFinding, dispatch, at, "owner-a", at.AddMinutes(1));

        var claim = await RetryAdmissionReservationStore.TryClaimStartSnapshotAsync(
            databasePath, goal.Id, task.Id, at, "owner-a", at.AddSeconds(1));
        var claimedReceipt = Assert.Single(claim!.Snapshot.Tasks.Single().RetryAdmissionHistory!);
        Assert.NotNull(claimedReceipt.WorkerStartClaimedAt);
        Assert.Null(claimedReceipt.WorkerStartedAt);

        var confirmed = await RetryAdmissionReservationStore.TryConfirmStartAsync(
            databasePath, goal.Id, task.Id, at, "owner-a", at.AddSeconds(2));
        Assert.NotNull(Assert.Single(confirmed!.Snapshot.Tasks.Single().RetryAdmissionHistory!).WorkerStartedAt);
        Assert.Equal(at, reservation!.Snapshot.Tasks.Single().LastDispatch!.DispatchedAt);
    }

    [Xunit.Fact]
    public async Task ReservationPersistsExactPreparedDispatchWithReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal("Atomic prepared retry", [task]);
        kernel.RetryTask(goal.Id, task.Id, "Retry source repair.", retryCause: RetryCause.NewSourceFinding);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(kernel);
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value, task.Id.Value, task.RequiredRole, "OpenAI", "gpt",
            PaidRouteClassification.Paid, "candidate", "criteria", [], [], [], [], "base", "main"));
        var at = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var preparedDispatch = new TaskDispatchRecord(
            "worker", "command", "worktree", at,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        _ = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint,
            PaidRouteClassification.Paid, RetryCause.NewSourceFinding, preparedDispatch, at,
            "owner-a", at.AddMinutes(1));

        var saved = await repository.LoadGoalAsync(goal.Id);
        Assert.Equal(at, saved!.Tasks.Single().LastDispatch!.DispatchedAt);
    }

    [Xunit.Fact]
    public async Task RecoveryOwnerFencesExpiredOriginalOwnerAtStartClaim()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retry-admission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fence recovered retry", [task]);
        kernel.RetryTask(goal.Id, task.Id, "Retry source repair.", retryCause: RetryCause.NewSourceFinding);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        await repository.SaveAsync(kernel);
        var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            goal.Id.Value, task.Id.Value, task.RequiredRole, "OpenAI", "gpt",
            PaidRouteClassification.Paid, "candidate", "criteria", [], [], [], [], "base", "main"));
        var at = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var dispatch = new TaskDispatchRecord(
            "worker", "command", "worktree", at,
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);
        _ = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid,
            RetryCause.NewSourceFinding, dispatch, at, "owner-a", at.AddMinutes(1));
        var recovery = await RetryAdmissionReservationStore.TryReserveAsync(
            databasePath, goal.Id, task.Id, fingerprint, PaidRouteClassification.Paid,
            RetryCause.NewSourceFinding, dispatch, at.AddMinutes(2), "owner-b", at.AddMinutes(3),
            reservationRecoveryConfirmed: true);

        var originalClaim = await RetryAdmissionReservationStore.TryClaimStartAsync(
            databasePath, goal.Id, task.Id, at, "owner-a", at.AddMinutes(2));
        var recoveryClaim = await RetryAdmissionReservationStore.TryClaimStartAsync(
            databasePath, goal.Id, task.Id, at, "owner-b", at.AddMinutes(2));

        Assert.Equal(RetryAdmissionDecision.ResumedReservation, recovery!.Decision);
        Assert.False(originalClaim);
        Assert.True(recoveryClaim);
    }
}
