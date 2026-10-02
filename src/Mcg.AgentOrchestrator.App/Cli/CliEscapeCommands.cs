using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliEscapeCommands
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("escape", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        TextWriter? output = null)
    {
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            if (args.Count < 2 || !args[1].Equals("record", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(CliCommandHelp.EscapeUsage);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--goal", "--reason", "--evidence", "--found-by-goal", "--actor-kind",
                "--operator-actor", "--idempotency-key"
            };
            var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 2; index < args.Count; index++)
            {
                var flag = args[index];
                if (!allowed.Contains(flag)) throw new ArgumentException($"Unexpected escape argument '{flag}'.");
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{flag} requires a value.");
                if (!values.TryGetValue(flag, out var list)) values[flag] = list = [];
                list.Add(args[index]);
            }
            string? One(string flag) => values.TryGetValue(flag, out var list) ? list.LastOrDefault() : null;
            string Required(string flag) => !string.IsNullOrWhiteSpace(One(flag)) ? One(flag)! :
                throw new ArgumentException($"{flag} requires non-empty text.");
            var payload = new EscapeRecordOperatorIntentPayload(Required("--goal"), Required("--reason"),
                values.TryGetValue("--evidence", out var evidence) ? evidence : [],
                One("--found-by-goal"), Environment.CurrentDirectory);
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args,
                CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var id = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(id, One("--idempotency-key") ?? id,
                OperatorIntentVerbs.EscapeRecord, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], attribution.Actor,
                attribution.Channel, attribution.AuthenticationAssurance, DateTimeOffset.UtcNow,
                ActorKind: attribution.ActorKind);
            var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory,
                workspace.LogDirectory).EnqueueAsync(intent).GetAwaiter().GetResult();
            var writer = output ?? Console.Out;
            writer.WriteLine($"Operator intent queued: id={persisted.Id} verb={persisted.Verb} scope=workspace status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");
            if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory))
                writer.WriteLine(ConductorLoopLease.InactiveWarning);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
