namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LegacySqliteLockMessageClassifier
{
    internal static bool IsBusyOrLocked(Exception exception) =>
        exception.Message.Contains("SQLite Error 5", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("SQLite Error 6", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("database table is locked", StringComparison.OrdinalIgnoreCase);
}
