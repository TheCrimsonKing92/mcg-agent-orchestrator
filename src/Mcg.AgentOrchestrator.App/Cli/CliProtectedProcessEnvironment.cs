namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliProtectedProcessEnvironment
{
    internal const string ProtectedPidVariable = "MCG_ORCHESTRATOR_PROTECTED_PID";

    internal static void EnsureProtectedPid()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProtectedPidVariable)))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            ProtectedPidVariable,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
