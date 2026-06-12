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
        var rest = remainder.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 3 ? [command, rest[0], rest[1], rest[2]] : [command, .. rest];
    }

    if (command.Equals("retry", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("note", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
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
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("ask-goal", StringComparison.OrdinalIgnoreCase))
    {
        return [command, remainder];
    }

    if (command.Equals("ask", StringComparison.OrdinalIgnoreCase) || command.Equals("answer", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("verify", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("verify-manual", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 3 ? [command, rest[0], rest[1], rest[2]] : [command, .. rest];
    }

    if (command.Equals("dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-dispatch", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 3 ? [command, rest[0], rest[1], rest[2]] : [command, .. rest];
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

    if (command.Equals("add-task", StringComparison.OrdinalIgnoreCase))
    {
        var rest = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 2 ? [command, rest[0], rest[1]] : [command, .. rest];
    }

    if (command.Equals("agent", StringComparison.OrdinalIgnoreCase))
    {
        return [command, .. remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
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
}
