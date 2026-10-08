using Mcg.AgentOrchestrator.Infrastructure;

internal static class ExecutionTestSupport
{
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-orchestrator-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static SqliteOrchestratorStateRepository CreateMigratedStateRepository(
        string databasePath,
        Action<string>? statementObserver = null,
        SqliteWriteTelemetryOptions? telemetryOptions = null,
        Action? beforeOutboxCommit = null)
    {
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        return new SqliteOrchestratorStateRepository(
            databasePath,
            statementObserver,
            telemetryOptions,
            beforeOutboxCommit);
    }
}
