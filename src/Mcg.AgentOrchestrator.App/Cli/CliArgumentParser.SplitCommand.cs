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
        return SplitTaskTargetCommandWithTextFileFlag(command, remainder, 2);
    }

    if (command.Equals("retry", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("note", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommandWithTextFileFlag(command, remainder, 1);
    }

    if (command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("recover", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetTextCommandWithFileFlags(command, remainder, "--text-file");
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

        return SplitTargetTextCommandWithFileFlags(command, remainder, "--text-file");
    }

    if (command.Equals("attention", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length >= 4 &&
            rest[0].Equals("answer", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeAttentionClarificationId(rest[2])
            ? [command, rest[0], rest[1], rest[2], rest[3]]
            : [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("verify", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommand(command, remainder, 1);
    }

    if (command.Equals("verify-manual", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTaskTargetCommandWithTextFileFlag(command, remainder, 2);
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

    if (command.Equals("backlog-add", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetTextCommandWithFileFlags(command, remainder, "--body-file", "--text-file");
    }

    if (command.Equals("backlog-close", StringComparison.OrdinalIgnoreCase))
    {
        return SplitTargetTextCommandWithFileFlags(command, remainder, "--reason-file", "--text-file");
    }

    if (command.Equals("goal", StringComparison.OrdinalIgnoreCase))
    {
        return SplitObjectiveCommandWithFlags(command, remainder);
    }

    if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
    {
        // stop <goal-prefix> <reason text> [--as <mode>] [--confirm-*]
        // First word = goal prefix; reason = text up to first ' --'; rest = flags
        var stopPrefixEnd = remainder.IndexOf(' ');
        if (stopPrefixEnd < 0)
        {
            return [command, remainder];
        }

        var stopGoalPrefix = remainder[..stopPrefixEnd];
        var stopRest = remainder[(stopPrefixEnd + 1)..].Trim();
        var stopFlagIndex = stopRest.IndexOf(" --", StringComparison.Ordinal);
        if (stopFlagIndex < 0 && stopRest.StartsWith("--", StringComparison.Ordinal))
        {
            stopFlagIndex = 0;
        }

        if (stopFlagIndex < 0)
        {
            return [command, stopGoalPrefix, stopRest];
        }

        var stopReason = stopRest[..stopFlagIndex].Trim();
        var stopFlags = stopRest[stopFlagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [command, stopGoalPrefix, stopReason, .. stopFlags];
    }

    if (command.Equals("dashboard", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("operator-listen", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    if (command.Equals("simple-goal", StringComparison.OrdinalIgnoreCase))
    {
        return SplitObjectiveCommandWithFlags(command, remainder);
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
        return SplitTargetTextCommandWithFileFlags(command, remainder, "--text-file");
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

    return IsSimpleCommand(command)
        ? SplitSimpleCommandWithFlags(command, remainder)
        : [command, remainder];
}

private static IReadOnlyList<string> SplitSimpleCommandWithFlags(string command, string remainder)
{
    var flagIndex = remainder.IndexOf(" --", StringComparison.Ordinal);
    if (flagIndex < 0 && remainder.StartsWith("--", StringComparison.Ordinal))
    {
        flagIndex = 0;
    }

    if (flagIndex < 0)
    {
        return [command, .. remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)];
    }

    var beforeFlags = remainder[..flagIndex].Trim();
    var flags = remainder[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (string.IsNullOrWhiteSpace(beforeFlags))
    {
        return [command, .. flags];
    }

    return [command, .. beforeFlags.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries), .. flags];
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

private static IReadOnlyList<string> SplitTaskTargetCommandWithTextFileFlag(string command, string remainder, int trailingArgumentCount)
{
    var flagIndex = IndexOfAnyFileFlag(remainder, "--text-file");
    if (flagIndex < 0)
    {
        return SplitTaskTargetCommand(command, remainder, trailingArgumentCount);
    }

    var beforeFlag = remainder[..flagIndex].Trim();
    var flags = remainder[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return string.IsNullOrWhiteSpace(beforeFlag)
        ? [command, .. flags]
        : [.. SplitTaskTargetCommand(command, beforeFlag, trailingArgumentCount), .. flags];
}

private static bool LooksLikePositionalGoalTask(string first, string second)
{
    return !first.StartsWith("--", StringComparison.Ordinal) &&
        (!int.TryParse(first, out _) || first.Length >= 8) &&
        int.TryParse(second, out _);
}

private static IReadOnlyList<string> SplitTargetTextCommandWithFileFlags(string command, string remainder, params string[] fileFlags)
{
    var targetEnd = remainder.IndexOf(' ');
    if (targetEnd < 0)
    {
        return [command, remainder];
    }

    var target = remainder[..targetEnd];
    var rest = remainder[(targetEnd + 1)..].Trim();
    var flagIndex = IndexOfAnyFileFlag(rest, fileFlags);

    if (flagIndex < 0)
    {
        return [command, target, rest];
    }

    var inlineText = rest[..flagIndex].Trim();
    var flags = rest[flagIndex..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return string.IsNullOrWhiteSpace(inlineText)
        ? [command, target, .. flags]
        : [command, target, inlineText, .. flags];
}

private static int IndexOfAnyFileFlag(string text, params string[] fileFlags)
{
    var best = -1;
    foreach (var fileFlag in fileFlags)
    {
        var index = text.IndexOf($" {fileFlag}", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            index++;
        }
        else if (text.StartsWith(fileFlag, StringComparison.OrdinalIgnoreCase))
        {
            index = 0;
        }

        if (index >= 0 && (best < 0 || index < best))
        {
            best = index;
        }
    }

    return best;
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
    return string.IsNullOrWhiteSpace(objective)
        ? [command, .. flags]
        : [command, objective, .. flags];
}
}
