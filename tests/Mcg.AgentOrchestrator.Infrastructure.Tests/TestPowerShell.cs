using Mcg.AgentOrchestrator.Infrastructure;

internal static class TestPowerShell
{
    internal static string Executable => WorkerShell.Executable;

    internal static string ForTheoryToken(string token) =>
        token == "pwsh" ? Executable : token;
}
