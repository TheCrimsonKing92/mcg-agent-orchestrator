internal static class ProgramStartupErrorFormatter
{
    public static string Format(Exception exception) =>
        StartupFailureReport.HasSqliteException(exception) ? StartupFailureReport.Format(exception) :
        $"{exception.GetType().Name}: {Mcg.AgentOrchestrator.Infrastructure.StoreSchemaDiagnostics.Message(exception)}";
}
