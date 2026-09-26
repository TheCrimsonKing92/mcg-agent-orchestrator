using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

internal sealed class RetentionReclaimFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-retention-reclaim", Guid.NewGuid().ToString("N"));
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
    internal const string CompletedId = "11111111111111111111111111111111";
    internal const string ActiveId = "22222222222222222222222222222222";
    internal const string FailedId = "33333333333333333333333333333333";

    internal string ExecutionDirectory => _root;
    internal string OrchestratorDirectory => Path.Combine(_root, ".orchestrator");
    internal string LogDirectory => Path.Combine(OrchestratorDirectory, "logs");

    internal RetentionReclaimFixture()
    {
        Directory.CreateDirectory(LogDirectory);
    }

    internal StorageRetentionGoal Goal(string id, GoalStatus status) =>
        new(id, status, new Dictionary<string, WorkTaskStatus>());

    internal string AttemptRoot(string family = "acceptance-gate-attempts") =>
        Path.Combine(OrchestratorDirectory, family);

    internal string WriteAgedFile(string path, int days)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "retention evidence");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-days).UtcDateTime);
        return path;
    }

    internal string WriteAttempt(string goalId, string attemptId, int ordinal, int days, bool failed = false)
    {
        var goalDirectory = Path.Combine(AttemptRoot(), goalId);
        Directory.CreateDirectory(goalDirectory);
        var metadata = Path.Combine(goalDirectory, attemptId + ".attempt.json");
        File.WriteAllText(metadata, System.Text.Json.JsonSerializer.Serialize(new
        {
            attemptId,
            goalId,
            ordinal,
            startedAt = Now.AddDays(-days),
            outcome = failed ? 2 : 1,
            reconciledAt = Now.AddDays(-days)
        }));
        File.SetLastWriteTimeUtc(metadata, Now.AddDays(-days).UtcDateTime);
        var payload = Path.Combine(goalDirectory, attemptId, "payload.txt");
        return WriteAgedFile(payload, days);
    }

    internal StorageRetentionResult Run(StorageRetentionReclaimOptions? options = null, params StorageRetentionGoal[] goals) =>
        StorageRetentionMaintenance.Run(LogDirectory, OrchestratorDirectory, ExecutionDirectory, goals, Now,
            mtpResultsRoot: Path.Combine(_root, "empty-mtp"), reclaimOptions: options);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
