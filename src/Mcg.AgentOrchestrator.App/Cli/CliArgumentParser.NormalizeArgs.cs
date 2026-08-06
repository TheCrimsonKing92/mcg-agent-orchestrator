namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
public static IReadOnlyList<string> NormalizeArgs(string[] args)
{
    var command = args[0];

    if (args.Length == 1)
    {
        return [command];
    }

    if (command.Equals("progress", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 2, allowTextFile: true);
    }

    if (command.Equals("retry", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1, allowTextFile: true);
    }

    if (command.Equals("note", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("answer", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("gate-satisfied", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Any(arg => arg.Equals("--text-file", StringComparison.OrdinalIgnoreCase)))
        {
            return args;
        }

        return SplitCommand(string.Join(' ', args));
    }

    if (command.Equals("supersede", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Any(arg => arg.Equals("--text-file", StringComparison.OrdinalIgnoreCase)) || args.Length < 4)
        {
            return args;
        }

        return [command, args[1], args[2], string.Join(' ', args.Skip(3))];
    }

    if (command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("unpark-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("recover", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length >= 3
            ? NormalizeGoalDispositionCommand(args)
            : args;
    }

    if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length >= 3
            ? NormalizeStopAliasCommand(args)
            : args;
    }

    if (command.Equals("verification-plan", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1);
    }

    if (command.Equals("subscription-dispatch", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeFlagTextValueWithFileFlags(args, "--confirm-limit-review", "--text-file");
    }

    if (command.Equals("ask-goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, string.Join(' ', args.Skip(1))];
    }

    if (command.Equals("ask", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1);
    }

    if (command.Equals("attention", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length >= 4 &&
            args[1].Equals("answer", StringComparison.OrdinalIgnoreCase) &&
            args.Any(arg => arg.Equals("--text-file", StringComparison.OrdinalIgnoreCase)))
        {
            return NormalizeAttentionAnswerWithFileFlag(args);
        }

        if (args.Length >= 5 &&
            args[1].Equals("answer", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeAttentionClarificationId(args[3]))
        {
            return [command, args[1], args[2], args[3], string.Join(' ', args.Skip(4))];
        }

        if (args.Length >= 4 && args[1].Equals("answer", StringComparison.OrdinalIgnoreCase))
        {
            return [command, args[1], args[2], string.Join(' ', args.Skip(3))];
        }

        return args;
    }

    if (command.Equals("backlog-add", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTargetTextCommandWithFileFlags(args, 1, "--body-file", "--text-file");
    }

    if (command.Equals("backlog-update", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTargetCommandWithValueFlags(args, 1, "--title", "--description", "--priority", "--tags", "--status");
    }

    if (command.Equals("goal-amend", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTargetCommandWithValueFlags(args, 1, "--waive", "--reason", "--reason-file", "--text-file", "--actor");
    }

    if (command.Equals("backlog-close", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTargetTextCommandWithFileFlags(args, 1, "--reason-file", "--text-file");
    }

    if (command.Equals("backlog-annotate", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTargetTextCommandWithFileFlags(args, 1, "--text-file");
    }

    if (command.Equals("backlog-reopen", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length >= 3
            ? [command, args[1], string.Join(' ', args.Skip(2))]
            : args;
    }

    if (command.Equals("verify", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1);
    }

    if (command.Equals("verify-manual", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 2, allowTextFile: true);
    }

    if (command.Equals("dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-dispatch", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 2);
    }

    if (command.Equals("worker-profile", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length >= 3
            ? [command, args[1], string.Join(' ', args.Skip(2))]
            : args;
    }

    if (command.Equals("worker-profile-export", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-import", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-check", StringComparison.OrdinalIgnoreCase))
    {
        return args;
    }

    if (command.Equals("dashboard", StringComparison.OrdinalIgnoreCase))
    {
        return args;
    }

    if (command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("operator-listen", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("codex-egress-proxy", StringComparison.OrdinalIgnoreCase))
    {
        return args;
    }

    if (command.Equals("goal", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeObjectiveCommandWithFlags(args);
    }

    if (command.Equals("simple-goal", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeObjectiveCommandWithFlags(args);
    }

    if (command.Equals("lifecycle-simple-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("lifecycle-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("goal-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("backlog-intake", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("intent-template", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeObjectiveCommandWithFlags(args);
    }

    if (command.Equals("add-task", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 3)
        {
            return args;
        }

        var targetIndex = args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
        if (targetIndex >= args.Length)
        {
            return args;
        }

        var beforeRoleIndex = Array.FindIndex(
            args,
            targetIndex + 1,
            arg => arg.Equals("--before-role", StringComparison.OrdinalIgnoreCase));
        var textFileIndex = Array.FindIndex(
            args,
            targetIndex + 1,
            arg => arg.Equals("--text-file", StringComparison.OrdinalIgnoreCase));
        if (beforeRoleIndex < 0 || (textFileIndex >= 0 && textFileIndex < beforeRoleIndex))
        {
            return NormalizeTargetTextCommandWithFileFlags(args, targetIndex, "--text-file");
        }

        var normalized = args.Take(targetIndex + 1).ToList();
        if (beforeRoleIndex > targetIndex + 1)
        {
            normalized.Add(string.Join(' ', args.Skip(targetIndex + 1).Take(beforeRoleIndex - targetIndex - 1)));
        }

        normalized.AddRange(args.Skip(beforeRoleIndex));
        return normalized;
    }

    if (command.Equals("agent", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("agent-add", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeAgentArgs(args);
    }

    if (IsSimpleCommand(command))
    {
        return args;
    }

    return [command, string.Join(' ', args.Skip(1))];
}

private static IReadOnlyList<string> NormalizeAttentionAnswerWithFileFlag(string[] args)
{
    var textStartIndex = args.Length >= 5 && LooksLikeAttentionClarificationId(args[3])
        ? 4
        : 3;
    return NormalizeTargetTextCommandWithFileFlags(args, textStartIndex - 1, "--text-file");
}

private static IReadOnlyList<string> NormalizeTargetCommandWithValueFlags(string[] args, int targetIndex, params string[] valueFlags)
{
    if (args.Length <= targetIndex + 1)
        return args;

    var result = new List<string> { args[0], args[targetIndex] };
    for (var i = targetIndex + 1; i < args.Length; i++)
    {
        var token = args[i];
        if (!valueFlags.Any(flag => flag.Equals(token, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(token);
            continue;
        }

        result.Add(token);
        var value = new List<string>();
        for (i++; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i--;
                break;
            }

            value.Add(args[i]);
        }

        if (value.Count > 0)
            result.Add(string.Join(' ', value));
    }

    return result;
}

private static IReadOnlyList<string> NormalizeFlagTextValueWithFileFlags(string[] args, string valueFlag, params string[] fileFlags)
{
    var flagIndex = Array.FindIndex(args, 1, arg => arg.Equals(valueFlag, StringComparison.OrdinalIgnoreCase));
    if (flagIndex < 0)
    {
        return args;
    }

    var valueStartIndex = flagIndex + 1;
    if (args.Length <= valueStartIndex)
    {
        return args;
    }

    var fileFlagIndex = Array.FindIndex(args, valueStartIndex, arg => fileFlags.Any(flag => arg.Equals(flag, StringComparison.OrdinalIgnoreCase)));
    if (fileFlagIndex >= 0)
    {
        var parts = args.Take(valueStartIndex).ToList();
        if (fileFlagIndex > valueStartIndex)
        {
            parts.Add(string.Join(' ', args.Skip(valueStartIndex).Take(fileFlagIndex - valueStartIndex)));
        }

        parts.AddRange(args.Skip(fileFlagIndex));
        return parts;
    }

    var nextFlagIndex = Array.FindIndex(args, valueStartIndex, arg => arg.StartsWith("--", StringComparison.Ordinal));
    if (nextFlagIndex < 0)
    {
        return [.. args.Take(valueStartIndex), string.Join(' ', args.Skip(valueStartIndex))];
    }

    if (nextFlagIndex == valueStartIndex)
    {
        return args;
    }

    return [.. args.Take(valueStartIndex), string.Join(' ', args.Skip(valueStartIndex).Take(nextFlagIndex - valueStartIndex)), .. args.Skip(nextFlagIndex)];
}

private static IReadOnlyList<string> NormalizeAgentArgs(string[] args)
{
    if (args.Length < 5)
    {
        return args;
    }

    var parts = new List<string> { args[0], args[1], args[2], args[3] };
    var flagIndex = Array.FindIndex(args, 4, arg => arg.StartsWith("--", StringComparison.Ordinal));
    if (flagIndex < 0)
    {
        parts.Add(string.Join(' ', args.Skip(4)));
        return parts;
    }

    if (flagIndex > 4)
    {
        parts.Add(string.Join(' ', args.Skip(4).Take(flagIndex - 4)));
    }

    parts.AddRange(args.Skip(flagIndex));
    return parts;
}

private static IReadOnlyList<string> NormalizeTargetTextCommandWithFileFlags(string[] args, int targetIndex, params string[] fileFlags)
{
    var textStartIndex = targetIndex + 1;
    if (args.Length <= textStartIndex)
    {
        return args;
    }

    var flagIndex = Array.FindIndex(args, textStartIndex, arg => fileFlags.Any(flag => arg.Equals(flag, StringComparison.OrdinalIgnoreCase)));
    if (flagIndex < 0)
    {
        return [.. args.Take(textStartIndex), string.Join(' ', args.Skip(textStartIndex))];
    }

    var parts = args.Take(textStartIndex).ToList();
    if (flagIndex > textStartIndex)
    {
        parts.Add(string.Join(' ', args.Skip(textStartIndex).Take(flagIndex - textStartIndex)));
    }

    parts.AddRange(args.Skip(flagIndex));
    return parts;
}

private static IReadOnlyList<string> NormalizeGoalDispositionCommand(string[] args)
{
    var parts = NormalizeTargetTextCommandWithFileFlags(args, 1, "--text-file");
    if (parts.Count != 3 ||
        !TryGetGoalDispositionConfirmationFlag(args[0], out var confirmationFlag))
    {
        return parts;
    }

    var text = parts[2];
    var confirmationIndex = IndexOfStandaloneFlag(text, confirmationFlag);
    if (confirmationIndex < 0)
    {
        return parts;
    }

    var reason = text[..confirmationIndex].Trim();
    return string.IsNullOrWhiteSpace(reason)
        ? [parts[0], parts[1], confirmationFlag]
        : [parts[0], parts[1], reason, confirmationFlag];
}

private static IReadOnlyList<string> NormalizeStopAliasCommand(string[] args)
{
    var parts = new List<string> { args[0], args[1] };
    var reasonTokens = new List<string>();
    var flagParts = new List<string>();

    for (var index = 2; index < args.Length; index++)
    {
        var arg = args[index];
        if (IsStopAliasValueFlag(arg) && index + 1 < args.Length)
        {
            flagParts.Add(arg);
            flagParts.Add(args[++index]);
            continue;
        }

        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            flagParts.Add(arg);
            continue;
        }

        reasonTokens.Add(arg);
    }

    if (reasonTokens.Count > 0)
    {
        parts.Add(string.Join(' ', reasonTokens));
    }

    parts.AddRange(flagParts);
    return parts;
}

private static bool IsStopAliasValueFlag(string arg) =>
    arg.Equals("--as", StringComparison.OrdinalIgnoreCase) ||
    arg.Equals("--text-file", StringComparison.OrdinalIgnoreCase);

private static IReadOnlyList<string> NormalizeTaskTargetArgs(string[] args, int trailingArgumentCount, bool allowTextFile = false)
{
    if (args.Length <= 1)
    {
        return args;
    }

    if (allowTextFile && args.Any(arg => arg.Equals("--text-file", StringComparison.OrdinalIgnoreCase)))
    {
        return args;
    }

    var remainder = string.Join(' ', args.Skip(1));
    return allowTextFile
        ? SplitTaskTargetCommandWithTextFileFlag(args[0], remainder, trailingArgumentCount)
        : SplitTaskTargetCommand(args[0], remainder, trailingArgumentCount);
}

private static IReadOnlyList<string> NormalizeObjectiveCommandWithFlags(string[] args)
{
    var flagIndex = Array.FindIndex(args, 1, arg => arg.StartsWith("--", StringComparison.Ordinal));
    if (flagIndex < 0)
    {
        return [args[0], string.Join(' ', args.Skip(1))];
    }

    var objective = string.Join(' ', args.Skip(1).Take(flagIndex - 1));
    return string.IsNullOrWhiteSpace(objective)
        ? [args[0], .. args.Skip(flagIndex)]
        : [args[0], objective, .. args.Skip(flagIndex)];
}

private static bool LooksLikeAttentionClarificationId(string value)
{
    if (value.Length > 0 && value.Length <= 8 && value.All(Uri.IsHexDigit))
        return true;
    if (value.StartsWith("spec-clarification:", StringComparison.Ordinal))
        return true;

    return value.Contains('-', StringComparison.Ordinal) &&
           value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
}
