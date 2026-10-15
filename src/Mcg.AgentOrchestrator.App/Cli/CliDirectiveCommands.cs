using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliDirectiveCommands
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        (args[0].Equals("directive", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("directives", StringComparison.OrdinalIgnoreCase));

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        TextWriter? output = null, TimeProvider? time = null)
    {
        var writer = output ?? Console.Out;
        time ??= TimeProvider.System;
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            if (args[0].Equals("directives", StringComparison.OrdinalIgnoreCase))
            {
                var options = Parse(args, 1, "--json");
                var now = time.GetUtcNow();
                var rows = new SqliteOperatorDirectiveStore(workspace.OperatorDirectivesStorePath).List(now);
                if (options.ContainsKey("--json"))
                    writer.WriteLine(DirectivesAgentReadView.Render(rows, time));
                else if (rows.Count == 0)
                    writer.WriteLine("No owner directives recorded.");
                else
                    foreach (var row in rows)
                    {
                        var expiry = row.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ??
                            (row.UntilHandoff ? "until handoff" : "none");
                        writer.WriteLine($"{row.Id} | {row.Kind} | {row.RecordedAt.ToString("O", CultureInfo.InvariantCulture)} | {row.Actor} | expiry={expiry}" +
                            (row.Expired ? " | expired" : ""));
                        writer.WriteLine(row.Text);
                    }
                return 0;
            }
            args = OperatorIntentSubmissionOutput.StripJsonFlag(args, out var json);
            if (args.Count < 2) throw new ArgumentException(CliCommandHelp.DirectiveUsage);
            object payload;
            string verb;
            Dictionary<string, string?> parsed;
            switch (args[1].ToLowerInvariant())
            {
                case "record":
                    parsed = Parse(args, 2, "--kind", "--text-file", "--expires-at", "--until-handoff",
                        "--actor-kind", "--operator-actor", "--idempotency-key");
                    if (parsed.ContainsKey("--expires-at") && parsed.ContainsKey("--until-handoff"))
                        throw new ArgumentException("--expires-at and --until-handoff cannot be combined.");
                    payload = new DirectiveRecordOperatorIntentPayload(Required(parsed, "--kind"),
                        File.ReadAllText(Required(parsed, "--text-file")), parsed.GetValueOrDefault("--expires-at"),
                        parsed.ContainsKey("--until-handoff"));
                    verb = OperatorIntentVerbs.DirectiveRecord;
                    break;
                case "retire":
                    if (args.Count < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException(CliCommandHelp.DirectiveUsage);
                    parsed = Parse(args, 3, "--reason", "--actor-kind", "--operator-actor", "--idempotency-key");
                    payload = new DirectiveRetireOperatorIntentPayload(args[2], Required(parsed, "--reason"));
                    verb = OperatorIntentVerbs.DirectiveRetire;
                    break;
                case "handoff":
                    parsed = Parse(args, 2, "--note", "--actor-kind", "--operator-actor", "--idempotency-key");
                    payload = new DirectiveHandoffOperatorIntentPayload(parsed.GetValueOrDefault("--note"));
                    verb = OperatorIntentVerbs.DirectiveHandoff;
                    break;
                default: throw new ArgumentException(CliCommandHelp.DirectiveUsage);
            }
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args,
                CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var id = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(id, parsed.GetValueOrDefault("--idempotency-key") ?? id,
                verb, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options), [],
                attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, time.GetUtcNow(),
                ActorKind: attribution.ActorKind);
            var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory,
                workspace.LogDirectory).EnqueueAsync(intent).GetAwaiter().GetResult();
            OperatorIntentSubmissionOutput.PrintWorkspace(persisted, workspace.OrchestratorDirectory, json, writer);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string?> Parse(IReadOnlyList<string> args, int start, params string[] permitted)
    {
        var allowed = permitted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = start; index < args.Count; index++)
        {
            var flag = args[index];
            if (!allowed.Contains(flag)) throw new ArgumentException($"Unexpected directive argument '{flag}'.");
            if (flag.Equals("--until-handoff", StringComparison.OrdinalIgnoreCase) ||
                flag.Equals("--json", StringComparison.OrdinalIgnoreCase))
                values[flag] = null;
            else
            {
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{flag} requires a value.");
                values[flag] = args[index];
            }
        }
        return values;
    }

    private static string Required(Dictionary<string, string?> values, string flag) =>
        !string.IsNullOrWhiteSpace(values.GetValueOrDefault(flag)) ? values[flag]! :
            throw new ArgumentException($"{flag} requires non-empty text.");
}
