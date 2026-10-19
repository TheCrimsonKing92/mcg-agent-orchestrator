using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateCancelCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals(OperatorIntentVerbs.GateCancel, StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            args = OperatorIntentSubmissionOutput.StripJsonFlag(args, out var json);
            if (args.Count < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(CliCommandHelp.GateCancelUsage);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 2; index < args.Count; index++)
            {
                var flag = args[index];
                if (flag.ToLowerInvariant() is not ("--text-file" or "--attempt" or "--idempotency-key" or "--operator-actor" or "--actor-kind"))
                    throw new ArgumentException(CliCommandHelp.GateCancelUsage);
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{flag} requires a value. {CliCommandHelp.GateCancelUsage}");
                values[flag] = args[index];
            }
            var attemptId = values.GetValueOrDefault("--attempt");
            if (attemptId is not null && (attemptId.Length is < 1 or > 64 ||
                attemptId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw new ArgumentException("invalid-attempt-id");
            var path = values.GetValueOrDefault("--text-file");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new ArgumentException("gate-cancel requires an existing --text-file.");
            var reason = File.ReadAllText(path).Trim();
            if (reason.Length == 0) throw new ArgumentException("gate-cancel requires a nonempty --text-file.");
            var prefix = args[1];
            var matches = !File.Exists(workspace.SqliteStatePath) ? [] :
                SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .ListGoalIdStatusesAsync().GetAwaiter().GetResult()
                    .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(goal => goal.Id).Order(StringComparer.Ordinal).ToArray();
            if (matches.Length == 0) throw new ArgumentException($"unknown-goal: {prefix}");
            if (matches.Length > 1) throw new ArgumentException($"ambiguous-goal-prefix: {string.Join(", ", matches)}");
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args,
                CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var id = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(id, values.GetValueOrDefault("--idempotency-key") ?? id,
                OperatorIntentVerbs.GateCancel, matches[0], null,
                JsonSerializer.Serialize(new GateCancelOperatorIntentPayload(reason, attemptId), OperatorIntentJson.Options), [],
                attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, time.GetUtcNow(),
                ActorKind: attribution.ActorKind);
            var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
                .EnqueueAsync(intent).GetAwaiter().GetResult();
            OperatorIntentSubmissionOutput.PrintAnswer(persisted, workspace.OrchestratorDirectory, matches[0], json);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
    }
}
