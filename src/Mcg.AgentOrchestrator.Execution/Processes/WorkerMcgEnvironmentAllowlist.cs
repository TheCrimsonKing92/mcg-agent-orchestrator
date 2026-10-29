using System.Collections.Frozen;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class WorkerMcgEnvironmentAllowlist
{
    public const string OrchestratorDotnetPathVariable = "MCG_ORCHESTRATOR_DOTNET_PATH";

    public static IReadOnlySet<string> Names { get; } = new[]
    {
        DispatchProcessHost.StartGatePathVariable,
        WorkerSandboxOptions.DispatchWorkerVariable,
        DotnetBuildStorageLayout.IsolatedRootOverrideVariable,
        OrchestratorDotnetPathVariable
    }.ToFrozenSet(StringComparer.Ordinal);

    public static void RemoveDisallowed(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var name in environment.Keys.ToArray())
        {
            if (name.StartsWith("MCG_", StringComparison.Ordinal) && !Names.Contains(name))
                environment.Remove(name);
        }
    }
}
