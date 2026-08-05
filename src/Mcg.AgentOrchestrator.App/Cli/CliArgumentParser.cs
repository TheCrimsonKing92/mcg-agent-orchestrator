using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
public sealed record RefreshDispatchOptions(
    IReadOnlyList<string> TargetParts,
    bool IncludeHistory,
    int? HistoryLimit);

public static RefreshDispatchOptions ParseRefreshDispatchOptions(IReadOnlyList<string> parts, string usage)
{
    var targetParts = new List<string>();
    var includeHistory = false;
    int? historyLimit = null;

    for (var index = 0; index < parts.Count; index++)
    {
        var part = parts[index];
        if (part.Equals("--history", StringComparison.OrdinalIgnoreCase))
        {
            includeHistory = true;
            continue;
        }

        if (!part.Equals("--history-limit", StringComparison.OrdinalIgnoreCase))
        {
            targetParts.Add(part);
            continue;
        }

        if (historyLimit is not null || index + 1 >= parts.Count ||
            !int.TryParse(parts[++index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedLimit) ||
            parsedLimit <= 0)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        historyLimit = parsedLimit;
        includeHistory = true;
    }

    return new RefreshDispatchOptions(targetParts, includeHistory, historyLimit);
}

public static void RequirePartCount(IReadOnlyList<string> parts, int expected, string usage)
{
    if (parts.Count < expected)
    {
        throw new ArgumentException($"Usage: {usage}");
    }
}
}


