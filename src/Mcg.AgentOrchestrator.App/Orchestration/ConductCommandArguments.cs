namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductCommandArguments
{
    internal static bool IsLoop(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase))
            return false;

        return args.Any(a =>
            a.Equals("--loop", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--watch", StringComparison.OrdinalIgnoreCase));
    }

    internal static int ResolvePollSeconds(IReadOnlyList<string> parts)
    {
        if (GetFlagValue(parts, "--poll-seconds") is { } pollSeconds)
        {
            return ParsePositiveInteger(pollSeconds, "--poll-seconds");
        }

        if (HasCliConfirmation(parts, "--poll-seconds"))
        {
            throw new ArgumentException("--poll-seconds requires a positive integer value.");
        }

        if (GetFlagValue(parts, "--watch-interval") is { } watchInterval)
        {
            return ParsePositiveInteger(watchInterval, "--poll-seconds");
        }

        if (HasCliConfirmation(parts, "--watch-interval"))
        {
            throw new ArgumentException("--poll-seconds requires a positive integer value.");
        }

        return ConductorBatchLoop.DefaultWatchIntervalSeconds;
    }

    private static string? GetFlagValue(IReadOnlyList<string> parts, string flag)
    {
        for (var i = 1; i < parts.Count; i++)
        {
            var inlinePrefix = flag + "=";
            if (parts[i].StartsWith(inlinePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return parts[i][inlinePrefix.Length..];
            }

            if (parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < parts.Count ? parts[i + 1] : null;
            }
        }

        return null;
    }

    private static int ParsePositiveInteger(string value, string flag)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{flag} requires a positive integer value.");
        }

        return parsed;
    }

    private static bool HasCliConfirmation(IReadOnlyList<string> parts, string flag)
    {
        return parts.Any(part => part.Equals(flag, StringComparison.OrdinalIgnoreCase));
    }
}
