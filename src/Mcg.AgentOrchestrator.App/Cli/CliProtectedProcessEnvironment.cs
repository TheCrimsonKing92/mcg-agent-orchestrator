using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliProtectedProcessEnvironment
{
    internal const string ProtectedPidVariable = ProtectedProcessIdentity.PidVariable;
    internal const string ProtectedStartTicksVariable = ProtectedProcessIdentity.StartTicksVariable;

    internal static void EnsureProtectedPid()
    {
        var resolved = ProtectedProcessIdentity.ResolveAtStartup(
            Environment.GetEnvironmentVariable(ProtectedPidVariable),
            Environment.GetEnvironmentVariable(ProtectedStartTicksVariable),
            ProtectedProcessIdentity.Current(), ProtectedProcessIdentity.ReadLiveStartTicks);
        ProtectedProcessIdentity.Bind(resolved, Environment.ProcessId);
    }
}
