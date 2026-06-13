namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
public static IReadOnlyList<string> SplitCommand(string line)
{
    var firstSpace = line.IndexOf(' ');
    if (firstSpace < 0)
    {
        return [line];
    }

    var command = line[..firstSpace];
    var remainder = line[(firstSpace + 1)..].Trim();

    if (command.Equals("progress", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 2);
    }

    if (command.Equals("retry", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("note", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 1);
    }

    if (command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("subscription-dispatch", StringComparison.OrdinalIgnoreCase))
    {
        const string limitReviewFlag = "--confirm-limit-review";
        var flagIndex = remainder.IndexOf(limitReviewFlag, StringComparison.OrdinalIgnoreCase);
        if (flagIndex >= 0)
        {
            var before = remainder[..flagIndex].Trim();
            var note = remainder[(flagIndex + limitReviewFlag.Length)..].Trim();
            var beforeParts = string.IsNullOrWhiteSpace(before)
                ? []
                : before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.IsNullOrWhiteSpace(note)
                ? [command, .. beforeParts, limitReviewFlag]
                : [command, .. beforeParts, limitReviewFlag, note];
        }
    }

    if (command.Equals("verification-plan", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 1);
    }

    if (command.Equals("ask-goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, remainder];
    }

    if (command.Equals("ask", StringComparison.OrdinalIgnoreCase) || command.Equals("answer", StringComparison.OrdinalIgnoreCase))
    {
        if (command.Equals("ask", StringComparison.OrdinalIgnoreCase))
        {
            return SplitTaskTargetCommand(command, remainder, 1);
        }

        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("verify", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 1);
    }

    if (command.Equals("verify-manual", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 2);
    }

    if (command.Equals("dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-dispatch", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 2);
    }

    if (command.Equals("worker-profile", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("worker-profile-export", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-import", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-check", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("dashboard", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("simple-goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, remainder];
    }

    if (command.Equals("lifecycle-simple-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("lifecycle-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("goal-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("backlog-intake", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("intent-template", StringComparison.OrdinalIgnoreCase))
    {
        return SplitObjectiveCommandWithFlags(command, remainder);
    }

    if (command.Equals("add-task", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("agent", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("agent-add", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeAgentArgs([command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
    }

    if (command.Equals("tasks", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    var simple = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    return IsSimpleCommand(command)
        ? [command, .. simple]
        : [command, remainder];
}

private static IReadOnlyList<string> SplitTaskTargetCommand(string command, string remainder, int trailingArgumentCount)
{
    var maxParts = 2 + trailingArgumentCount;
    var rest = remainder.Split(' ', maxParts, StringSplitOptions.RemoveEmptyEntries);
    if (rest.Length == maxParts && LooksLikePositionalGoalTask(rest[0], rest[1]))
    {
        return [command, .. rest];
    }

    var goalFlagMaxParts = 3 + trailingArgumentCount;
    var goalFlagRest = remainder.Split(' ', goalFlagMaxParts, StringSplitOptions.RemoveEmptyEntries);
    if (goalFlagRest.Length == goalFlagMaxParts && goalFlagRest[0].Equals("--goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. goalFlagRest];
    }

    var legacyRest = remainder.Split(' ', 1 + trailingArgumentCount, StringSplitOptions.RemoveEmptyEntries);
    return [command, .. legacyRest];
}

private static bool LooksLikePositionalGoalTask(string first, string second)
{
    return !first.StartsWith("--", StringComparison.Ordinal) &&
        !int.TryParse(first, out _) &&
        int.TryParse(second, out _);
}

private static IReadOnlyList<string> SplitObjectiveCommandWithFlags(string command, string remainder)
{
    var flagIndex = remainder.IndexOf(" --", StringComparison.Ordinal);
    if (flagIndex < 0 && remainder.StartsWith("--", StringComparison.Ordinal))
    {
        flagIndex = 0;
    }

    if (flagIndex < 0)
    {
        return [command, remainder];
    }

    var objective = remainder[..flagIndex].Trim();
    var flags = remainder[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return [command, objective, .. flags];
}
}
