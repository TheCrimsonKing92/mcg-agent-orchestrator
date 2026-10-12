using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ExperimentArmStamp
{
    internal static IReadOnlyDictionary<string, string>? Resolve(string worktreeOrExecutionDirectory)
    {
        try
        {
            var directory = Path.Combine(
                AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreeOrExecutionDirectory), ".orchestrator");
            var storePath = Path.Combine(directory, "experiments.db");
            if (!File.Exists(storePath)) return null;
            var experiments = ExperimentStore.OpenReadOnly(storePath).ListOpenAsync().GetAwaiter().GetResult();
            var arms = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var experiment in experiments)
            {
                if (experiment.Spec.Baseline.Kind != ExperimentBaselineKind.AlternatingGates ||
                    experiment.Spec.Intervention is not
                        { Kind: ExperimentInterventionKind.ConfigFlag, FlagTarget: { } target }) continue;
                var value = ReadFlag(directory, target);
                if (value is { } flag) arms.Add(experiment.Id, flag == target.ValueToApply ? "on" : "off");
            }
            return arms.Count == 0 ? null : arms;
        }
        catch { return null; } // A failed observation must not fault or suppress host telemetry.
    }

    private static bool? ReadFlag(string directory, ExperimentFlagTarget target)
    {
        var fileName = target.FileKind switch
        {
            ExperimentFlagFileKind.ConductorPolicy => "conductor-policy.json",
            ExperimentFlagFileKind.RemoteLaneExecutors => "remote-lane-executors.json",
            _ => null
        };
        if (fileName is null) return null;
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        // The existing readers default absent properties to false; absence is not an observed arm.
        if (target.FileKind == ExperimentFlagFileKind.ConductorPolicy)
        {
            if (!root.TryGetProperty(target.PropertyName, out var property) ||
                property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            return ConductorPolicyBooleanFlags.Read(ConductorAutonomyPolicy.ParseJson(json), target.PropertyName);
        }
        if (!root.TryGetProperty("focusedEvidence", out var block) || block.ValueKind != JsonValueKind.Object ||
            !block.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String ||
            mode.GetString() is not ("off" or "shadow")) return null;
        return RemoteLaneExecutorFlags.Read(path, target.PropertyName);
    }
}
