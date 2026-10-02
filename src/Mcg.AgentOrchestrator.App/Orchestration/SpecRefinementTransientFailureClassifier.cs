namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SpecRefinementTransientFailureClassifier
{
    private static readonly HashSet<string> WritePhases = new(StringComparer.Ordinal)
    {
        "attach-snapshot", "record-policy-receipt", "emit-clarification-event"
    };

    public static bool IsTransient(string? detail)
    {
        if (detail is null)
            return false;

        var separator = detail.IndexOf(" detail=", StringComparison.Ordinal);
        if (separator < 0)
            return false;

        var phases = detail[..separator].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("phase=", StringComparison.Ordinal)).ToArray();
        if (phases.Length != 1 || !WritePhases.Contains(phases[0]["phase=".Length..]))
            return false;

        var failure = detail[(separator + " detail=".Length)..];
        return failure.Contains("SQLite Error 5:", StringComparison.Ordinal) ||
            failure.Contains("SQLite Error 6:", StringComparison.Ordinal);
    }
}
