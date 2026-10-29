using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Display evidence only; this classification never participates in a gate verdict.
internal sealed record OwnerConsoleGateTrxOutcomes(bool Available,
    IReadOnlyList<string> FailureTestNames, int SkippedCount, int PassedCount, int TotalCount)
{
    internal static OwnerConsoleGateTrxOutcomes Unavailable { get; } = new(false, [], 0, 0, 0);
    internal IReadOnlyList<string> FailureNames { get; } = FailureTestNames
        .Select(name => string.Join(".", name.Split('(')[0].Split('.').TakeLast(2))).ToArray();
    internal int FailureCount => FailureTestNames.Count;
    internal bool AllPassed => Available && TotalCount > 0 && PassedCount == TotalCount;

    internal static OwnerConsoleGateTrxOutcomes Read(string path)
    {
        try
        {
            var results = XDocument.Load(path).Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
            var failures = new List<string>();
            var skipped = 0;
            var passed = 0;
            foreach (var result in results)
            {
                switch (((string?)result.Attribute("outcome"))?.Trim().ToUpperInvariant())
                {
                    case "FAILED": case "ERROR": case "TIMEOUT":
                        failures.Add((string?)result.Attribute("testName") ?? "");
                        break;
                    case "NOTEXECUTED": case "SKIPPED": skipped++; break;
                    case "PASSED": passed++; break;
                }
            }
            return new(true, failures, skipped, passed, results.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return Unavailable; }
    }
}
