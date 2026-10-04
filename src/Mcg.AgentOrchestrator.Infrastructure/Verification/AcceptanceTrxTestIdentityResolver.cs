using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTrxTestIdentityResolver
{
    internal static string? Resolve(XElement result, XElement? definition)
    {
        var testName = result.Attribute("testName")?.Value?.Trim();
        var testMethod = definition?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("TestMethod", StringComparison.Ordinal));
        var className = testMethod?.Attribute("className")?.Value?.Trim();
        var methodName = testMethod?.Attribute("name")?.Value?.Trim();
        var displayName = result.Descendants()
            .Concat(definition?.Descendants() ?? [])
            .Where(element =>
                element.Name.LocalName.Equals("DisplayName", StringComparison.Ordinal) ||
                element.Name.LocalName.Equals("Description", StringComparison.Ordinal))
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        displayName ??= definition?.Attribute("name")?.Value?.Trim();

        if (!string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(methodName))
        {
            var suffix = DataCaseSuffix(testName) ?? DataCaseSuffix(displayName);
            return $"{className}.{methodName}{suffix}";
        }

        if (!string.IsNullOrWhiteSpace(displayName) &&
            (string.IsNullOrWhiteSpace(testName) ||
                (LooksLikeQualifiedTestName(testName) && displayName.Length < testName.Length)))
        {
            return displayName;
        }

        return string.IsNullOrWhiteSpace(testName) ? null : testName;
    }

    internal static string NormalizeSelector(string identity)
    {
        var normalized = identity.Trim();
        var parameterStart = normalized.IndexOfAny(['(', '[']);
        return parameterStart < 0 ? normalized : normalized[..parameterStart];
    }

    private static string DataCaseSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parameterStart = value.IndexOfAny(['(', '[']);
        return parameterStart < 0 ? string.Empty : value[parameterStart..].Trim();
    }

    private static bool LooksLikeQualifiedTestName(string value) =>
        value.Contains('+', StringComparison.Ordinal) ||
        value.Count(ch => ch == '.') >= 2;

    internal static IReadOnlyList<string> ExtractTrxFailureIdentities(string trxPath)
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
                AcceptanceTrxOutcomeTaxonomy.IsFatal(element.Attribute("outcome")?.Value))
            .Select(result =>
            {
                definitionsByTestId.TryGetValue(
                    result.Attribute("testId")?.Value ?? string.Empty,
                    out var definition);
                return Resolve(result, definition) ??
                    result.Attribute("testId")?.Value?.Trim() ??
                    "unknown test";
            })
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
