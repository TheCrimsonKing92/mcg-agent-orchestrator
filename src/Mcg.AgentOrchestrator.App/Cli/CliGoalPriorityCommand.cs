using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalPriorityCommand
{
    internal const string Usage = "Usage: goal-priority <goal-prefix> <low|normal|high|critical> [--reason <text>] [--idempotency-key <key>] [--json]";

    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(OperatorIntentVerbs.GoalPriority, StringComparison.OrdinalIgnoreCase);

    internal static bool Execute(IReadOnlyList<string> parts, CliExecutionContext context, IOperatorIntentStore? intentStore = null)
    {
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        parts = OperatorIntentSubmissionOutput.StripJsonFlag(parts, out var emitJson);
        if (parts.Count < 3 || parts[1].StartsWith("--"))
            throw new ArgumentException(Usage);
        if (!GoalPriorities.TryParse(parts[2], out var priority))
            throw new ArgumentException($"Invalid goal priority '{parts[2]}'; valid levels: {GoalPriorities.ValidLevelsText}.");
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, parts[1]);
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(
            parts, CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
        var id = Guid.NewGuid().ToString("N");
        var payload = new GoalPriorityOperatorIntentPayload(GoalPriorities.Format(priority),
            GetFlagValue(parts, "--reason") ?? "");
        var intent = new OperatorIntentRecord(id,
            GetFlagValue(parts, "--idempotency-key") ?? id,
            OperatorIntentVerbs.GoalPriority, goal.Id.Value, null,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [],
            attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance,
            DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
        var persisted = (intentStore ?? SqliteOperatorIntentStore.ForDirectories(
            context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory))
            .EnqueueAsync(intent).GetAwaiter().GetResult();
        if (emitJson)
        {
            // Preserve the submitted-intent envelope and add this command's payload.
            var document = System.Text.Json.Nodes.JsonNode.Parse(OperatorIntentAgentReadView.Render(persisted))!;
            document["payload"]!["payload"] = JsonSerializer.SerializeToNode(
                JsonSerializer.Deserialize<GoalPriorityOperatorIntentPayload>(persisted.PayloadJson, OperatorIntentJson.Options),
                OperatorIntentJson.Options);
            Console.WriteLine(document.ToJsonString());
            if (!ConductorLoopLease.IsActive(context.Workspace.OrchestratorDirectory))
                Console.Error.WriteLine(ConductorLoopLease.InactiveWarning);
        }
        else
            OperatorIntentSubmissionOutput.PrintAnswer(persisted, context.Workspace.OrchestratorDirectory, goal.Id.Value, false);
        return false;
    }

    private static string? GetFlagValue(IReadOnlyList<string> parts, string flag)
    {
        for (var i = 3; i < parts.Count; i++)
        {
            if (parts[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return parts[i][(flag.Length + 1)..];
            if (!parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= parts.Count || parts[i + 1].StartsWith("--"))
                throw new ArgumentException($"{flag} requires a value.");
            return parts[i + 1];
        }
        return null;
    }
}
