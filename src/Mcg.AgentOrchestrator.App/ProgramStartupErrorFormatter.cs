internal static class ProgramStartupErrorFormatter
{
    public static string Format(Exception exception) =>
        $"{exception.GetType().Name}: {Mcg.AgentOrchestrator.Infrastructure.StoreSchemaDiagnostics.Message(exception)}";
}
