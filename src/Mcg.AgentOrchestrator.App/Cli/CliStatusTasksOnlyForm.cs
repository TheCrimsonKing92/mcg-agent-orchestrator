namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliStatusTasksOnlyForm
{
    internal static bool IsForm(IReadOnlyList<string> args)
    {
        if (args.Count is < 2 or > 3 ||
            !args[0].Equals("status", StringComparison.OrdinalIgnoreCase) ||
            CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        var flagCount = 0;
        foreach (var argument in args.Skip(1))
        {
            if (argument.Equals("--tasks-only", StringComparison.OrdinalIgnoreCase))
                flagCount++;
            else if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith("-", StringComparison.Ordinal))
                return false;
        }

        return flagCount == 1;
    }

    internal static string? ResolveGoalPrefix(IReadOnlyList<string> args) =>
        CliSingleGoalReportSelector.ResolveGoalPrefix(args, (parts, flags) =>
            parts.Skip(1).FirstOrDefault(argument => !flags.Contains(argument, StringComparer.OrdinalIgnoreCase)));
}
