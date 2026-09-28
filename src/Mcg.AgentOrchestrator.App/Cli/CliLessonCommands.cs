using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliLessonCommands
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && (args[0].Equals("lesson", StringComparison.OrdinalIgnoreCase) ||
                           args[0].Equals("lessons", StringComparison.OrdinalIgnoreCase));

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        TextWriter? output = null)
    {
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            var writer = output ?? Console.Out;
            if (args[0].Equals("lessons", StringComparison.OrdinalIgnoreCase))
            {
                var options = Parse(args, 1, "--applies-to", "--all", "--json");
                var lessons = new SqliteOperatorLessonStore(workspace.OperatorLessonsStorePath)
                    .List(options.Has("--all"), options.One("--applies-to"));
                if (options.Has("--json"))
                    writer.WriteLine(JsonSerializer.Serialize(lessons, OperatorIntentJson.Options));
                else if (lessons.Count == 0)
                    writer.WriteLine("No lessons recorded.");
                else
                    foreach (var lesson in lessons)
                        writer.WriteLine($"{lesson.Id} | {(lesson.RetiredAt is null ? "active" : "retired")} | {lesson.Rule}" +
                            (lesson.RetiredAt is null ? string.Empty : $" | reason={lesson.RetireReason}"));
                return 0;
            }
            if (args.Count < 2) throw new ArgumentException(CliCommandHelp.LessonUsage);
            var action = args[1].ToLowerInvariant();
            object payload;
            string verb;
            Options parsed;
            if (action == "record")
            {
                parsed = Parse(args, 2, "--situation", "--rule", "--evidence", "--applies-to",
                    "--goal", "--actor-kind", "--operator-actor", "--idempotency-key");
                var goal = parsed.One("--goal");
                payload = new LessonRecordOperatorIntentPayload(
                    parsed.Required("--situation"), parsed.Required("--rule"),
                    parsed.All("--evidence"), parsed.All("--applies-to"),
                    goal is null ? null : ResolveGoalId(workspace, goal), Environment.CurrentDirectory);
                verb = OperatorIntentVerbs.LessonRecord;
            }
            else if (action == "retire")
            {
                if (args.Count < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(CliCommandHelp.LessonUsage);
                parsed = Parse(args, 3, "--reason", "--evidence", "--actor-kind",
                    "--operator-actor", "--idempotency-key");
                payload = new LessonRetireOperatorIntentPayload(args[2], parsed.Required("--reason"),
                    parsed.All("--evidence"), Environment.CurrentDirectory);
                verb = OperatorIntentVerbs.LessonRetire;
            }
            else throw new ArgumentException(CliCommandHelp.LessonUsage);
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args,
                CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var id = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(id, parsed.One("--idempotency-key") ?? id,
                verb, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options),
                [], attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance,
                DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
            var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory,
                workspace.LogDirectory).EnqueueAsync(intent).GetAwaiter().GetResult();
            writer.WriteLine($"Operator intent queued: id={persisted.Id} verb={persisted.Verb} scope=workspace status={persisted.Status}; poll with operator-intent-status {persisted.Id}.");
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

    private static string ResolveGoalId(OrchestratorWorkspace workspace, string prefix)
    {
        if (!File.Exists(workspace.SqliteStatePath))
            throw new ArgumentException($"Goal '{prefix}' was not found.");
        var matches = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException($"Goal prefix '{prefix}' matched {matches.Length} goals.");
        return matches[0].Id;
    }

    private static Options Parse(IReadOnlyList<string> args, int start, params string[] permitted)
    {
        var allowed = permitted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var index = start; index < args.Count; index++)
        {
            var flag = args[index];
            if (!allowed.Contains(flag)) throw new ArgumentException($"Unexpected lesson argument '{flag}'.");
            if (!values.TryGetValue(flag, out var list)) values[flag] = list = [];
            if (flag is "--all" or "--json") continue;
            if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{flag} requires a value.");
            list.Add(args[index]);
        }
        return new Options(values);
    }

    private sealed record Options(Dictionary<string, List<string>> Values)
    {
        public bool Has(string flag) => Values.ContainsKey(flag);
        public string? One(string flag) => Values.TryGetValue(flag, out var list) ? list.LastOrDefault() : null;
        public string Required(string flag) =>
            !string.IsNullOrWhiteSpace(One(flag)) ? One(flag)! : throw new ArgumentException($"{flag} requires non-empty text.");
        public IReadOnlyList<string> All(string flag) => Values.TryGetValue(flag, out var list) ? list : [];
    }
}
