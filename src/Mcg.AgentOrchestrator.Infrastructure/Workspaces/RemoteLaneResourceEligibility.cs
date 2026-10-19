namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteLaneResourceEligibility
{
    internal static string? ExclusionReason(IReadOnlyList<string> keys, bool requiresBuildSystemChange,
        IReadOnlyList<string> machineLocalKeys)
    {
        if (keys.Count == 0) return null;
        var foreignKey = keys.FirstOrDefault(key => !machineLocalKeys.Contains(key, StringComparer.Ordinal));
        if (foreignKey is not null) return "exclusive-key:" + foreignKey;
        return requiresBuildSystemChange ? "build-system-change" : null;
    }
}
