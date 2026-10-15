namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaneResultsDirectory
{
    internal static string? FromArguments(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--results-directory", StringComparison.OrdinalIgnoreCase))
                continue;

            return index + 1 < arguments.Count && !string.IsNullOrWhiteSpace(arguments[index + 1])
                ? arguments[index + 1]
                : null;
        }

        return null;
    }

    internal static IAcceptanceLaneProgressProbe? ProbeFor(IReadOnlyList<string> arguments) =>
        FromArguments(arguments) is { } directory ? new TrxStreamProgressProbe(directory) : null;
}
