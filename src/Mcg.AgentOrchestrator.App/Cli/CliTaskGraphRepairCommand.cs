using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliTaskGraphRepairCommand
{
    internal const string Usage = "Usage: repair-task-graph [--goal <prefix>] --plan-file <path> [--idempotency-key <key>] [--json]";
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        (args[0].Equals("add-task", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals(OperatorIntentVerbs.TaskGraphRepair, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> Normalize(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        var flags = new List<string>();
        var prefixCount = args.Count >= 3 && args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        for (var i = prefixCount; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--")) { positional.Add(args[i]); continue; }
            flags.Add(args[i]);
            if (!args[i].Equals("--json", StringComparison.OrdinalIgnoreCase) && !args[i].Contains('='))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--"))
                    throw new ArgumentException($"{args[i]} requires a value.");
                flags.Add(args[++i]);
            }
        }
        return positional.Count > 1
            ? [.. args.Take(prefixCount), positional[0], string.Join(' ', positional.Skip(1)), .. flags]
            : [.. args.Take(prefixCount), .. positional, .. flags];
    }

    internal static bool Execute(IReadOnlyList<string> args, ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, ref Goal? currentGoal,
        CliPersistentStateRunner.OperatorIntentSubmissionSource source)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var selector = Flag(args, "--goal");
        var id = currentGoal?.Id;
        if (selector is not null)
        {
            var matches = repository.ListGoalIdStatusesAsync().GetAwaiter().GetResult()
                .Where(goal => goal.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new ArgumentException($"Goal selector '{selector}' matched {matches.Length} goals; use a unique prefix.");
            id = new GoalId(matches[0].Id);
        }
        if (id is null) throw new ArgumentException("Select a goal with --goal <prefix>.");
        var snapshot = repository.LoadGoalAsync(id).GetAwaiter().GetResult()
            ?? throw new KeyNotFoundException($"Goal '{id.Value}' was not found.");
        currentGoal = AgentOrchestratorKernel.FromSnapshot(new([snapshot], [])).GetGoal(id);
        return Submit(args, workspace, currentGoal, source);
    }

    internal static bool Submit(IReadOnlyList<string> args, OrchestratorWorkspace workspace, Goal goal,
        CliPersistentStateRunner.OperatorIntentSubmissionSource source = CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        args = OperatorIntentSubmissionOutput.StripJsonFlag(args, out var json);
        TaskGraphRepairOperatorIntentPayload plan;
        if (args[0].Equals(OperatorIntentVerbs.TaskGraphRepair, StringComparison.OrdinalIgnoreCase))
        {
            var path = Flag(args, "--plan-file") ?? throw new ArgumentException(Usage);
            try
            {
                plan = JsonSerializer.Deserialize<TaskGraphRepairOperatorIntentPayload>(File.ReadAllText(path),
                    new JsonSerializerOptions(OperatorIntentJson.Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                    ?? throw new ArgumentException("invalid-plan: plan file is empty.");
            }
            catch (JsonException ex) { throw new ArgumentException($"invalid-plan-json: {ex.Message}", ex); }
        }
        else
        {
            var values = Positional(args);
            var file = Flag(args, "--text-file");
            if (values.Count < 1 || file is null && values.Count != 2 || file is not null && values.Count != 1)
                throw new ArgumentException(CliCommandHelp.AddTaskUsage);
            plan = new([new(values[0], file is null ? values[1] : File.ReadAllText(file), Flag(args, "--before-role"))]);
        }
        // Canonical contents, never path or current graph version, define bare replay.
        plan = plan with
        {
            Insertions = plan.Insertions?.Select(item => item is null ? item! : item with
            {
                Role = CanonicalRole(item.Role), Description = item.Description?.Trim()!,
                BeforeRole = item.BeforeRole is null ? null : CanonicalRole(item.BeforeRole)
            }).ToArray()!,
            ExpectedFinalRoleOrder = plan.ExpectedFinalRoleOrder?.Select(CanonicalRole).ToArray()
        };
        var payload = JsonSerializer.Serialize(plan, OperatorIntentJson.Options);
        var key = Flag(args, "--idempotency-key") ?? "graph-repair:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goal.Id.Value + "\n" + payload)));
        var actor = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args, source);
        var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), key, OperatorIntentVerbs.TaskGraphRepair,
            goal.Id.Value, null, payload, [], actor.Actor, actor.Channel, actor.AuthenticationAssurance,
            DateTimeOffset.UtcNow, ActorKind: actor.ActorKind);
        var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(intent).GetAwaiter().GetResult();
        OperatorIntentSubmissionOutput.PrintGraphRepair(persisted, workspace.OrchestratorDirectory, json);
        return false;
    }

    private static string CanonicalRole(string value) => Enum.TryParse<AgentRole>(value, true, out var role) && Enum.IsDefined(role)
        ? role.ToString() : value;

    private static List<string> Positional(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var i = 1; i < args.Count; i++)
            if (args[i].StartsWith("--")) { if (!args[i].Contains('=')) i++; }
            else result.Add(args[i]);
        return result;
    }

    private static string? Flag(IReadOnlyList<string> args, string flag)
    {
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(flag.Length + 1)..];
            if (!args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--")) throw new ArgumentException($"{flag} requires a value.");
            return args[i + 1];
        }
        return null;
    }
}
