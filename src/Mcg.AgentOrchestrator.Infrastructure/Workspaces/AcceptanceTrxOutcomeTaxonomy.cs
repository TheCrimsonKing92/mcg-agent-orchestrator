namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTrxOutcomeTaxonomy
{
    private static readonly (string Outcome, string Counter)[] FatalOutcomes =
    [
        ("Failed", "failed"),
        ("Error", "error"),
        ("Timeout", "timeout"),
        ("Aborted", "aborted"),
        ("NotRunnable", "notRunnable")
    ];

    internal static bool IsFatal(string? outcome) =>
        !string.IsNullOrWhiteSpace(outcome) &&
        FatalOutcomes.Any(fatal => fatal.Outcome.Equals(outcome.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static bool HasFatalCounter(Func<string, int?> readCounter) =>
        FatalOutcomes.Any(fatal => (readCounter(fatal.Counter) ?? 0) != 0);
}
