namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorGateProgressLog
{
    internal static void TryAppend(ConductEventLogWriter writer, string? goalId, string detail, string eventKind)
    {
        try
        {
            _ = writer.AppendRequired(eventKind, goalId, detail);
        }
        catch (Exception)
        {
            // Progress is observational; a log failure must not change a gate outcome.
        }
    }
}
