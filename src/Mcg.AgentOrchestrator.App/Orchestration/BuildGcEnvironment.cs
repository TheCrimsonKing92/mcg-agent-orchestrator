using System.Globalization;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BuildGcEnvironment
{
    internal const string ConserveMemoryVariable = "DOTNET_GCConserveMemory";
    internal const string HeapCountVariable = "DOTNET_GCHeapCount";

    internal static IReadOnlyDictionary<string, string> For(ConductorAutonomyPolicy policy)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (policy.BuildGcConserveMemory)
            variables[ConserveMemoryVariable] = policy.BuildGcConserveMemoryLevel.ToString(CultureInfo.InvariantCulture);
        if (policy.BuildGcHeapCount > 0)
            variables[HeapCountVariable] = policy.BuildGcHeapCount.ToString("X", CultureInfo.InvariantCulture);
        return variables;
    }

    internal static IReadOnlyDictionary<string, string> ForPolicyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try { return For(ConductorAutonomyPolicy.ParseJson(json)); }
        catch (Exception) { return new Dictionary<string, string>(); }
    }

    internal static IReadOnlyDictionary<string, string> ForStatePath(string statePath)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            return For(ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(directory));
        }
        catch (Exception) { return new Dictionary<string, string>(); }
    }

    internal static void Apply(IDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (key, value) in variables) environment[key] = value;
    }
}
