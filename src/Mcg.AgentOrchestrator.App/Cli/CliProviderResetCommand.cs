using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliProviderResetCommand
{
    internal const string Usage = "Usage: provider-reset <agent-id|provider|binding-key> <ISO-8601 instant with Z or offset> --text-file <path> [--idempotency-key <key>] [--json]";

    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(OperatorIntentVerbs.ProviderReset, StringComparison.OrdinalIgnoreCase);

    internal static bool Execute(IReadOnlyList<string> parts, CliExecutionContext context, IOperatorIntentStore? intentStore = null)
    {
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        parts = OperatorIntentSubmissionOutput.StripJsonFlag(parts, out var emitJson);
        if (parts.Count < 3 || parts[1].StartsWith("--")) throw new ArgumentException(Usage);
        if (!ProviderBudgetHoldSchedules.TryParseInstant(parts[2], out var resetAt))
            throw new ArgumentException("provider-reset requires an ISO-8601 instant with Z or an explicit offset.");
        var textPath = GetFlagValue(parts, "--text-file");
        if (string.IsNullOrWhiteSpace(textPath) || !File.Exists(textPath))
            throw new ArgumentException("provider-reset requires an existing --text-file.");
        var rationale = File.ReadAllText(textPath).Trim();
        if (rationale.Length == 0) throw new ArgumentException("provider-reset requires a nonempty --text-file.");
        var target = parts[1];
        var agent = context.Agents.FirstOrDefault(item => item.Id.Value.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (agent is not null) target = agent.Model.ProviderName;
        var holds = new Dictionary<string, ProviderBudgetExhaustionHold>(StringComparer.OrdinalIgnoreCase);
        foreach (var dispatch in context.Kernel.Goals.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory))
        {
            var provider = dispatch.ProviderName ?? dispatch.WorkerName;
            if (!DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(context.Kernel.Goals,
                    provider, dispatch.ClaudeCredentialSourceDirectory, out var hold)) continue;
            if (provider.Equals(target, StringComparison.OrdinalIgnoreCase) || hold.BindingKey.Equals(target, StringComparison.OrdinalIgnoreCase))
                holds.TryAdd(hold.BindingKey, hold);
        }
        if (holds.Count == 0) throw new ArgumentException($"no-current-hold: {parts[1]}");
        if (holds.Count > 1) throw new ArgumentException($"ambiguous-binding: {string.Join(", ", holds.Keys.Order(StringComparer.OrdinalIgnoreCase))}");
        var selected = holds.Values.Single();
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(parts,
            CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
        var id = Guid.NewGuid().ToString("N");
        var intent = new OperatorIntentRecord(id, GetFlagValue(parts, "--idempotency-key") ?? id,
            OperatorIntentVerbs.ProviderReset, selected.SourceGoalId.Value, selected.SourceTaskId.Value,
            JsonSerializer.Serialize(new ProviderResetOperatorIntentPayload(selected.BindingKey, resetAt.ToUniversalTime(), rationale), OperatorIntentJson.Options),
            [], attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance,
            DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
        var persisted = (intentStore ?? SqliteOperatorIntentStore.ForDirectories(
            context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory)).EnqueueAsync(intent).GetAwaiter().GetResult();
        OperatorIntentSubmissionOutput.PrintAnswer(persisted, context.Workspace.OrchestratorDirectory, selected.SourceGoalId.Value, emitJson);
        return false;
    }

    private static string? GetFlagValue(IReadOnlyList<string> parts, string flag)
    {
        for (var i = 3; i < parts.Count; i++)
        {
            if (parts[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) return parts[i][(flag.Length + 1)..];
            if (!parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= parts.Count || parts[i + 1].StartsWith("--")) throw new ArgumentException($"{flag} requires a value.");
            return parts[i + 1];
        }
        return null;
    }
}
