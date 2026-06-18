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
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 2);
    }

    if (command.Equals("retry", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("note", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 1);
    }

    if (command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
    {
        return args.Length >= 3
            ? [command, args[1], string.Join(' ', args.Skip(2))]
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
            ? [command, args[1], string.Join(' ', args.Skip(2))]
            : args;
    }

    if (command.Equals("backlog-add", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeBacklogTextCommandWithFileFlag(args, "--body-file");
    }

    if (command.Equals("backlog-close", StringComparison.OrdinalIgnoreCase))
    {
        return NormalizeBacklogTextCommandWithFileFlag(args, "--reason-file");
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
        return NormalizeTaskTargetArgs(args, trailingArgumentCount: 2);
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
            ? [command, args[1], string.Join(' ', args.Skip(2))]
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

private static IReadOnlyList<string> NormalizeBacklogTextCommandWithFileFlag(string[] args, string fileFlag)
{
    if (args.Length < 3)
    {
        return args;
    }

    var flagIndex = Array.FindIndex(args, 2, arg => arg.Equals(fileFlag, StringComparison.OrdinalIgnoreCase));
    if (flagIndex < 0)
    {
        return [args[0], args[1], string.Join(' ', args.Skip(2))];
    }

    var parts = new List<string> { args[0], args[1] };
    if (flagIndex > 2)
    {
        parts.Add(string.Join(' ', args.Skip(2).Take(flagIndex - 2)));
    }

    parts.AddRange(args.Skip(flagIndex));
    return parts;
}

private static IReadOnlyList<string> NormalizeTaskTargetArgs(string[] args, int trailingArgumentCount)
{
    return args.Length > 1
        ? SplitTaskTargetCommand(args[0], string.Join(' ', args.Skip(1)), trailingArgumentCount)
        : args;
}

private static IReadOnlyList<string> NormalizeObjectiveCommandWithFlags(string[] args)
{
    var flagIndex = Array.FindIndex(args, 1, arg => arg.StartsWith("--", StringComparison.Ordinal));
    if (flagIndex < 0)
    {
        return [args[0], string.Join(' ', args.Skip(1))];
    }

    var objective = string.Join(' ', args.Skip(1).Take(flagIndex - 1));
    return [args[0], objective, .. args.Skip(flagIndex)];
}
}
