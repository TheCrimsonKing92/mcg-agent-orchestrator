namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
internal static IReadOnlyList<string> RecognizedCommands { get; } =
[
    "attention",
    "status",
    "doctor",
    "project",
    "provider-smoke",
    "prototype",
    "prototype-ui",
    "serve-dashboard",
    "hosted-dashboard",
    "simple-hosted-dashboard",
    "open-dashboard",
    "transcript",
    "monitor",
    "monitor-goal",
    "goal-events",
    "goal-timing",
    "dispatch-value",
    "portfolio",
    "portfolio-view",
    "epic-add",
    "epic-assign",
    "epic-list",
    "epic-suggest",
    "epic-suggestions",
    "project-add",
    "project-assign",
    "readiness",
    "goal-recovery",
    "dogfood-eval",
    "dogfood-log",
    "record-goal",
    "failure-triage",
    "retention-plan",
    "run-events-maintenance",
    "build-lease-cleanup",
    "firewall-setup",
    "repo-process-info",
    "repo-process-stop",
    "stable-slot-dotnet",
    "gate-status",
    "cleanup-status",
    "acceptance-queue",
    "drain-goals",
    "acceptance",
    "evidence",
    "stages",
    "gates",
    "verify-needed",
    "input-needed",
    "operator-inbox",
    "operator-inbox-ack",
    "operator-listen",
    "operator-control-plane",
    "operator-commands",
    "goal-diagnostics",
    "next",
    "subscription-plan",
    "advance",
    "advance-subscription",
    "run-goal",
    "lifecycle-simple-goal",
    "lifecycle-goal",
    "goal-depends",
    "goal-plan",
    "plan",
    "ideate",
    "intent-template",
    "delegate",
    "abandon-goal",
    "goal-mark-landed",
    "acceptance-repair",
    "acceptance-retry",
    "park-goal",
    "unpark-goal",
    "rollback-goal",
    "cancel-goal",
    "supersede-goal",
    "agents",
    "agent-add",
    "model-functions",
    "model-function-add",
    "timeline",
    "task-timeline",
    "worker-profiles",
    "worker-profile-export",
    "worker-profile-import",
    "worker-profile-check",
    "profile-dispatch",
    "profile-dispatch-ready",
    "subscription-dispatch",
    "subscription-dispatch-ready",
    "inquiry",
    "cross-goal-start-plan",
    "start-subscription-ready-goals",
    "start-subscription-ready",
    "run",
    "api-run",
    "retry",
    "reassign-agent",
    "re-delegate",
    "redelegate",
    "note",
    "verification-plan",
    "brief",
    "execute-dispatch",
    "start-dispatch",
    "start-dispatches",
    "reconcile",
    "refresh-dispatch",
    "refresh-dispatches",
    "logs",
    "cancel-dispatch",
    "verify-manual",
    "verifications",
    "tasks",
    "task",
    "workspace",
    "model-outcomes",
    "durations",
    "loop-health",
    "provenance",
    "accept",
    "config",
    "land",
    "conduct",
    "backlog-list",
    "backlog-triage",
    "backlog-add",
    "backlog-update",
    "backlog-show",
    "backlog-annotate",
    "backlog-close",
    "backlog-supersede",
    "backlog-unsupersede",
    "backlog-link",
    "backlog-reopen",
    "backlog-view",
    "goals-prune",
    "operator-channel",
    "goal-changes"
];

internal static bool IsRecognizedCommand(string command) =>
    RecognizedCommands.Contains(command, StringComparer.OrdinalIgnoreCase);

internal static string FormatUnknownCommandMessage(IReadOnlyList<string> parts)
{
    var token = UnknownCommandToken(parts);
    var suggestions = FindNearestCommands(parts);
    var message = $"Unknown command '{token}'.";
    if (suggestions.Count > 0)
    {
        message += $"{Environment.NewLine}Did you mean: {string.Join(", ", suggestions)}?";
    }

    return message + $"{Environment.NewLine}Run with --help to list commands and usage.";
}

private static bool IsSimpleCommand(string command)
{
    return IsRecognizedCommand(command);
}

private static string UnknownCommandToken(IReadOnlyList<string> parts)
{
    if (parts.Count >= 2 &&
        !parts[1].StartsWith("-", StringComparison.Ordinal) &&
        RecognizedCommands.Contains($"{parts[0]}-{parts[1]}", StringComparer.OrdinalIgnoreCase))
    {
        return $"{parts[0]} {parts[1]}";
    }

    return parts.Count == 0 ? "" : parts[0];
}

private static IReadOnlyList<string> FindNearestCommands(IReadOnlyList<string> parts)
{
    var targets = CandidateTargets(parts);
    var commands = RecognizedCommands
        .Append("--help")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var exactMatches = targets
        .SelectMany(target => commands.Where(command => command.Equals(target, StringComparison.OrdinalIgnoreCase)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToArray();
    if (exactMatches.Length > 0)
    {
        return exactMatches;
    }

    var prefixMatches = targets
        .SelectMany(target => commands.Where(command => command.StartsWith(target, StringComparison.OrdinalIgnoreCase)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToArray();
    if (prefixMatches.Length > 0)
    {
        return prefixMatches;
    }

    return commands
        .Select(command => new
        {
            Command = command,
            Distance = targets.Min(target => EditDistance(target, command)),
            Overlap = targets.Max(target => CommonSubsequenceLength(target, command)),
            LengthDelta = targets.Min(target => Math.Abs(target.Length - command.Length))
        })
        .Where(candidate => candidate.Distance <= 2)
        .OrderBy(candidate => candidate.Distance)
        .ThenBy(candidate => candidate.LengthDelta)
        .ThenByDescending(candidate => candidate.Overlap)
        .ThenBy(candidate => candidate.Command, StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .Select(candidate => candidate.Command)
        .ToArray();
}

private static IReadOnlyList<string> CandidateTargets(IReadOnlyList<string> parts)
{
    if (parts.Count == 0)
    {
        return [];
    }

    var targets = new List<string>();
    if (parts.Count >= 2 && !parts[1].StartsWith("-", StringComparison.Ordinal))
    {
        targets.Add($"{parts[0]}-{parts[1]}");
    }

    targets.Add(parts[0]);
    return targets;
}

private static int EditDistance(string left, string right)
{
    var previous = new int[right.Length + 1];
    var current = new int[right.Length + 1];
    for (var j = 0; j <= right.Length; j++)
    {
        previous[j] = j;
    }

    for (var i = 1; i <= left.Length; i++)
    {
        current[0] = i;
        for (var j = 1; j <= right.Length; j++)
        {
            var cost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
            current[j] = Math.Min(
                Math.Min(current[j - 1] + 1, previous[j] + 1),
                previous[j - 1] + cost);
        }

        (previous, current) = (current, previous);
    }

    return previous[right.Length];
}

private static int CommonSubsequenceLength(string left, string right)
{
    var previous = new int[right.Length + 1];
    var current = new int[right.Length + 1];
    for (var i = 1; i <= left.Length; i++)
    {
        for (var j = 1; j <= right.Length; j++)
        {
            current[j] = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1])
                ? previous[j - 1] + 1
                : Math.Max(previous[j], current[j - 1]);
        }

        (previous, current) = (current, previous);
        Array.Clear(current);
    }

    return previous[right.Length];
}
}
