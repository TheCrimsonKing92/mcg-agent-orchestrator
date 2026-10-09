namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalPrefixArguments
{
    internal static string? GetOptionalArgument(IReadOnlyList<string> parts, params string[] flags)
    {
        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            if (flags.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                if (IsValueFlag(part))
                {
                    i++;
                }

                continue;
            }

            if (IsValueFlag(part))
            {
                i++;
                continue;
            }

            if (part.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            return part;
        }

        return null;
    }

    internal static bool IsValueFlag(string part) =>
        part.Equals("--goal", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--backlog-coverage", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--backlog-item", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--pipeline", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--role", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--task", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--autonomy-policy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--policy", StringComparison.OrdinalIgnoreCase);
}
