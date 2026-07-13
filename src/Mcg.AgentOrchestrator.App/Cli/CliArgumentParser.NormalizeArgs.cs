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

    if (command.Equals("retry", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("note", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1, allowTextFile: true);
    }

    if (command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("recover", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length >= 3
            ? NormalizeTargetTextCommandWithFileFlags(args, 1, "--text-file")
            : args;
    }

    if (command.Equals("verification-plan", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1);
    }

    if (command.Equals("ask-goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, string.Join(' ', args.Skip(1))];
    }

    if (command.Equals("ask", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1);
    }

    if (command.Equals("answer", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length >= 3
            ? NormalizeTargetTextCommandWithFileFlags(args, 1, "--text-file")
            : args;
    }

    if (command.Equals("attention", StringComparison.OrdinalIgnoreCase))
    {
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

    if (command.Equals("backlog-close", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTargetTextCommandWithFileFlags(args, 1, "--reason-file", "--text-file");
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
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase))
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
        return args.Length >= 3
            ? NormalizeTargetTextCommandWithFileFlags(args, 1, "--text-file")
            : args;
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

private static IReadOnlyList<string> NormalizeTaskTargetArgs(string[] args, int trailingArgumentCount, bool allowTextFile = false)
{
    if (args.Length <= 1)
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
    return value.Length > 0 && value.Length <= 8 && value.All(Uri.IsHexDigit);
}
}
