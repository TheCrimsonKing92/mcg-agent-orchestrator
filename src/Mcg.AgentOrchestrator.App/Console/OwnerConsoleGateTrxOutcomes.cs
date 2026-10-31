using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Display evidence only; this classification never participates in a gate verdict.
internal sealed record OwnerConsoleGateTrxOutcomes(bool Available,
    IReadOnlyList<string> FailureTestNames, int SkippedCount, int PassedCount, int TotalCount)
{
    internal IReadOnlyList<string> PassedTestNames { get; init; } = [];
    internal static OwnerConsoleGateTrxOutcomes Unavailable { get; } = new(false, [], 0, 0, 0);
    internal IReadOnlyList<string> FailureNames { get; } = FailureTestNames
        .Select(name => string.Join(".", name.Split('(')[0].Split('.').TakeLast(2))).ToArray();
    internal int FailureCount => FailureTestNames.Count;
    internal bool AllPassed => Available && TotalCount > 0 && PassedCount == TotalCount;

    internal static OwnerConsoleGateTrxOutcomes Read(string path)
    {
        try
        {
            var document = XDocument.Load(path);
            var definitionsByTestId = document.Descendants()
                .Where(element => element.Name.LocalName == "UnitTest" &&
                    !string.IsNullOrWhiteSpace((string?)element.Attribute("id")))
                .GroupBy(element => (string)element.Attribute("id")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var results = document.Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
            var failures = new List<string>();
            var passes = new List<string>();
            var skipped = 0;
            var passed = 0;
            foreach (var result in results)
            {
                switch (((string?)result.Attribute("outcome"))?.Trim().ToUpperInvariant())
                {
                    case "FAILED": case "ERROR": case "TIMEOUT":
                        definitionsByTestId.TryGetValue((string?)result.Attribute("testId") ?? "", out var definition);
                        failures.Add(AcceptanceTrxTestIdentityResolver.Resolve(result, definition) ?? "");
                        break;
                    case "NOTEXECUTED": case "SKIPPED": skipped++; break;
                    case "PASSED":
                        passed++;
                        definitionsByTestId.TryGetValue((string?)result.Attribute("testId") ?? "", out var passedDefinition);
                        passes.Add(AcceptanceTrxTestIdentityResolver.Resolve(result, passedDefinition) ?? "");
                        break;
                }
            }
            return new(true, failures, skipped, passed, results.Length) { PassedTestNames = passes };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return Unavailable; }
    }
}
