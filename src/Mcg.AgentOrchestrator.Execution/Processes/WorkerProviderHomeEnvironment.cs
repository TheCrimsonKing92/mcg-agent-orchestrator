using System.Collections.Frozen;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Credential and hook homes reserved by provider environment seeding.</summary>
public static class WorkerProviderHomeEnvironment
{
    public static IReadOnlySet<string> Names { get; } = new[]
    {
        "CODEX_HOME", "CLAUDE_CONFIG_DIR", "GROK_HOME", "HERMES_HOME",
        HarnessHookRootContract.EnvironmentVariableName
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
