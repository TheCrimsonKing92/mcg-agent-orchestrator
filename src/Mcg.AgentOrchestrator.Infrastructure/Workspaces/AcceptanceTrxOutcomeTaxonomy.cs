using System.Xml.Linq;

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

    internal static IReadOnlyList<string> ExtractTrxFailureEvidence(string trxPath)
    {
        var document = XDocument.Load(trxPath, LoadOptions.None);
        var definitionsByTestId = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attribute("outcome")?.Value,
                    "Failed",
                    StringComparison.OrdinalIgnoreCase))
            .Select(result =>
            {
                definitionsByTestId.TryGetValue(
                    result.Attribute("testId")?.Value ?? string.Empty,
                    out var definition);
                var testName = AcceptanceTrxTestIdentityResolver.Resolve(result, definition)
                    ?? result.Attribute("testId")?.Value?.Trim()
                    ?? "unknown test";
                var message = result
                    .Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName.Equals("Message", StringComparison.Ordinal) &&
                        element.Ancestors().Any(ancestor =>
                            ancestor.Name.LocalName.Equals("ErrorInfo", StringComparison.Ordinal)))
                    ?.Value;
                return $"[FAIL] {testName}: {GoalAcceptanceVerifier.FirstNonEmptyLine(message) ?? "failure message unavailable"}";
            })
            .ToArray();
    }
}
