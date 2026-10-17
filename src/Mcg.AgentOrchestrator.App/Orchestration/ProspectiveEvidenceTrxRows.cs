using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProspectiveEvidenceTrxRows
{
    internal sealed record Row(
        string FullIdentity, string ShortIdentity, string? ClassName, string? Outcome, string Path)
    {
        internal bool Passed => Outcome == "Passed";
    }

    internal static IEnumerable<Row> Read(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest" && element.Attribute("id") is not null)
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
            var full = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
            if (full is null) continue;
            var className = definition?.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "TestMethod")?.Attribute("className")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(className)) className = null;
            var namespaceEnd = className?.LastIndexOf('.') ?? -1;
            var shortIdentity = namespaceEnd >= 0 && full.StartsWith(className!, StringComparison.Ordinal)
                ? full[(namespaceEnd + 1)..] : full;
            yield return new(full, shortIdentity, className, result.Attribute("outcome")?.Value, path);
        }
    }

    internal static Row[] Match(string selector, IEnumerable<Row> rows) =>
        rows.Where(row => ProspectiveEvidenceGateResolver.Matches(selector, row.FullIdentity) ||
            ProspectiveEvidenceGateResolver.Matches(selector, row.ShortIdentity)).ToArray();

    internal static IReadOnlyList<string>? AmbiguousClassNames(IEnumerable<Row> matches)
    {
        var classes = matches.Select(row => row.ClassName).OfType<string>()
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return classes.Length >= 2 ? classes : null;
    }

    internal static string FormatEntry(string selector, IReadOnlyList<string>? ambiguousClassNames) =>
        ambiguousClassNames is { Count: >= 2 }
            ? $"{selector} (ambiguous: {string.Join(", ", ambiguousClassNames)})" : selector;
}
