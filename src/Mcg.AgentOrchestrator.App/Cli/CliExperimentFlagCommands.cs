using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliExperimentFlagCommands
{
    internal static bool SubmitApply(IReadOnlyList<string> parts, OrchestratorWorkspace workspace)
    {
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        if (parts.Count < 2 || parts[1].StartsWith('-'))
            throw new ArgumentException(CliCommandHelp.ExperimentApplyFlagUsage);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < parts.Count; index += 2)
        {
            if (parts[index] is not ("--operator-actor" or "--actor-kind" or "--idempotency-key") ||
                index + 1 >= parts.Count || string.IsNullOrWhiteSpace(parts[index + 1]) ||
                parts[index + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(parts[index], parts[index + 1]))
                throw new ArgumentException("experiment-apply-flag: expected each supported option once with a value.");
        }
        if (!File.Exists(workspace.ExperimentStorePath))
            throw new InvalidOperationException($"Experiment '{parts[1]}' was not found.");
        var record = new ExperimentStore(workspace.ExperimentStorePath).ResolveAsync(parts[1]).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"Experiment '{parts[1]}' was not found.");
        if (record.Spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag || record.Spec.Intervention.FlagTarget is null)
            throw new ArgumentException("experiment-apply-flag: a config-flag experiment with a flag target is required.");
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(parts,
            CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
        var id = Guid.NewGuid().ToString("n");
        var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(new OperatorIntentRecord(id, options.GetValueOrDefault("--idempotency-key") ?? id,
                OperatorIntentVerbs.ExperimentApplyFlag, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(new ExperimentApplyFlagOperatorIntentPayload(record.Id), OperatorIntentJson.Options),
                [], attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, DateTimeOffset.UtcNow,
                ActorKind: attribution.ActorKind)).GetAwaiter().GetResult();
        Console.WriteLine($"Operator intent queued: id={persisted.Id} verb={persisted.Verb} scope=workspace status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory)) Console.WriteLine(ConductorLoopLease.InactiveWarning);
        return false;
    }
}
