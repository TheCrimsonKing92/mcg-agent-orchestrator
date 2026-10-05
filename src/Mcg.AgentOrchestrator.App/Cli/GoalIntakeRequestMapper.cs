using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record GoalIntakeRequestDescriptor(
    string RequestKey,
    string Fingerprint,
    string Mode,
    string Objective);

internal static class GoalIntakeRequestMapper
{
    public static GoalIntakeRequestDescriptor? Map(
        IReadOnlyList<string> args,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents)
    {
        var requestKey = GetFlagValue(args, "--request-key");
        if (requestKey is null)
        {
            if (HasFlag(args, "--request-key"))
                throw new ArgumentException("--request-key requires a non-empty value.");
            return null;
        }

        GoalIntakeRequestStore.ValidateRequestKey(requestKey);
        var command = args[0].ToLowerInvariant();
        var fromBacklog = command == "backlog-intake" || HasFlag(args, "--from-backlog");
        if (fromBacklog && !HasFlag(args, "--create-goal") && !HasFlag(args, "--create-simple-goal"))
        {
            throw new ArgumentException(
                "--request-key on backlog intake requires --create-goal or --create-simple-goal.");
        }
        var simple = command == "simple-goal" || HasFlag(args, "--simple") || HasFlag(args, "--create-simple-goal");
        var mode = fromBacklog
            ? simple ? "backlog-simple" : "backlog-full"
            : simple ? "simple" : "full";
        var objective = fromBacklog
            ? ResolveBacklogObjective(args, workspace)
            : ResolveObjective(args);

        var roleOverrides = args
            .Select((value, index) => (value, index))
            .Where(entry => IsRoleFlag(entry.value))
            .Select(entry => $"{entry.value.ToLowerInvariant()}={ValueAfter(args, entry.index)}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var assignedAgents = CliCommandHandlers.ApplyRoleAgentOverrides(args, agents)
            .GroupBy(agent => agent.Role)
            .Select(group => group.First())
            .OrderBy(agent => agent.Role)
            .ThenBy(agent => agent.Id.Value, StringComparer.Ordinal)
            .Select(agent => $"{agent.Role}={agent.Id.Value}")
            .ToArray();
        var payload = new
        {
            version = GoalIntakeRequestStore.CurrentFingerprintVersion,
            mode,
            objective,
            pipeline = simple ? "developer-only" : (GetFlagValue(args, "--pipeline") ?? "auto").ToLowerInvariant(),
            sourceBacklog = fromBacklog ? ResolveBacklogSource(args, workspace) : GetFlagValue(args, "--backlog-item"),
            backlogCoverage = GetFlagValue(args, "--backlog-coverage")?.ToLowerInvariant(),
            run = HasFlag(args, "--run"),
            dispatch = HasFlag(args, "--dispatch"),
            roleOverrides,
            assignedAgents
        };
        var canonical = JsonSerializer.Serialize(payload);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new GoalIntakeRequestDescriptor(requestKey, fingerprint, mode, objective);
    }

    private static string ResolveObjective(IReadOnlyList<string> args)
    {
        var filePath = GetFlagValue(args, "--brief-file") ?? GetFlagValue(args, "--text-file");
        if (filePath is not null)
            return File.ReadAllText(Path.GetFullPath(filePath));

        for (var index = 1; index < args.Count; index++)
        {
            if (IsValueFlag(args[index]))
            {
                index++;
                continue;
            }
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                return args[index];
        }

        throw new ArgumentException("Goal intake requires an objective.");
    }

    private static string ResolveBacklogObjective(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        var filters = BacklogFilters(args, workspace);
        if (filters.Count > 1)
            throw new ArgumentException("--request-key supports exactly one backlog item per goal intake.");
        var filter = filters.SingleOrDefault();
        var plan = BacklogIntakePlanner.Build(workspace.BacklogStorePath, filter, 2);
        if (plan.Items.Count != 1)
            throw new InvalidOperationException(
                $"Keyed backlog intake requires exactly one resolved item; matched={plan.Items.Count}.");
        return plan.Items[0].SuggestedObjective;
    }

    private static IReadOnlyList<string> BacklogFilters(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        var explicitItem = GetFlagValue(args, "--backlog-item");
        if (!string.IsNullOrWhiteSpace(explicitItem))
            return [BacklogItemIdPrefixResolver.Resolve(workspace.BacklogStorePath, explicitItem, explicitFlag: true)!.Id];

        var filters = new List<string>();
        for (var index = 1; index < args.Count; index++)
        {
            if (IsValueFlag(args[index]))
            {
                index++;
                continue;
            }
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                filters.Add(args[index]);
        }
        return filters;
    }

    private static string? ResolveBacklogSource(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        var explicitItem = GetFlagValue(args, "--backlog-item");
        if (string.IsNullOrWhiteSpace(explicitItem))
            return explicitItem ?? BacklogFilters(args, workspace).SingleOrDefault();

        var resolvedId = BacklogFilters(args, workspace).Single();
        // Preserve existing full-id fingerprints, including their original casing, for replay.
        return explicitItem.Equals(resolvedId, StringComparison.OrdinalIgnoreCase) ? explicitItem : resolvedId;
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(value => value.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetFlagValue(IReadOnlyList<string> args, string flag)
    {
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(flag.Length + 1)..];
            if (args[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[index + 1]
                    : null;
        }
        return null;
    }

    private static string ValueAfter(IReadOnlyList<string> args, int index) =>
        index + 1 < args.Count ? args[index + 1] : string.Empty;

    private static bool IsRoleFlag(string value) => value.ToLowerInvariant() is
        "--ideation" or "--researcher" or "--planner" or "--developer" or "--tester" or "--reviewer";

    private static bool IsValueFlag(string value) => value.ToLowerInvariant() is
        "--request-key" or "--brief-file" or "--text-file" or "--pipeline" or
        "--backlog-item" or "--backlog-coverage" or "--ideation" or "--researcher" or
        "--planner" or "--developer" or "--tester" or "--reviewer";
}
