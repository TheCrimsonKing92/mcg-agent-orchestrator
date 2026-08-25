using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RetryAdmissionReservationStoreTests
{
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

        var results = await Task.WhenAll(
            RetryAdmissionReservationStore.TryReserveAsync(
                databasePath, goal.Id, task.Id, fingerprint,
                PaidRouteClassification.Paid, RetryCause.NewSourceFinding, at, at),
            RetryAdmissionReservationStore.TryReserveAsync(
                databasePath, goal.Id, task.Id, fingerprint,
                PaidRouteClassification.Paid, RetryCause.NewSourceFinding, at.AddTicks(1), at.AddTicks(1)));

        Assert.Single(results, result => result?.Decision == RetryAdmissionDecision.Allowed);
        Assert.Single(results, result => result?.Decision == RetryAdmissionDecision.Prevented);
        var saved = await repository.LoadGoalAsync(goal.Id);
        Assert.Equal(2, saved!.Tasks.Single().RetryAdmissionHistory!.Count);
    }
}
