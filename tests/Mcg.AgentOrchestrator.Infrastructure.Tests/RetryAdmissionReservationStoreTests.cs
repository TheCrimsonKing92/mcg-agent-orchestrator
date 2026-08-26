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
