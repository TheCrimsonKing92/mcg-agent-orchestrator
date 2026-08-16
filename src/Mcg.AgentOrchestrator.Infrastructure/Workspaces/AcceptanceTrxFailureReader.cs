using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum AcceptanceTrxReadStatus
{
    Readable,
    Missing,
    Unreadable,
    Unparseable
}

internal sealed record AcceptanceTrxFailure(
    string? TestName,
    string Outcome,
    string? Message,
    string? StackTrace);

internal sealed record AcceptanceTrxReadResult(
    string Path,
    AcceptanceTrxReadStatus Status,
    IReadOnlyList<AcceptanceTrxFailure> Failures,
    string? Detail = null);

internal static class AcceptanceTrxFailureReader
{
    internal static AcceptanceTrxReadResult Read(string path)
    {
        if (!File.Exists(path))
        {
            return new AcceptanceTrxReadResult(path, AcceptanceTrxReadStatus.Missing, []);
        }

        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            return new AcceptanceTrxReadResult(
                path,
                AcceptanceTrxReadStatus.Unparseable,
                [],
                FirstNonEmptyLine(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AcceptanceTrxReadResult(
                path,
                AcceptanceTrxReadStatus.Unreadable,
                [],
                FirstNonEmptyLine(ex.Message));
        }

        var definitionsByTestId = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var failures = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal) &&
                !string.Equals(element.Attribute("outcome")?.Value?.Trim(), "Passed", StringComparison.OrdinalIgnoreCase))
            .Select(result =>
            {
                definitionsByTestId.TryGetValue(
                    result.Attribute("testId")?.Value ?? string.Empty,
                    out var definition);
                var errorInfo = result.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("ErrorInfo", StringComparison.Ordinal));
                return new AcceptanceTrxFailure(
                    ResolveTestName(result, definition),
                    result.Attribute("outcome")?.Value?.Trim() ?? "Unknown",
                    ElementValue(errorInfo, "Message"),
                    ElementValue(errorInfo, "StackTrace"));
            })
            .ToArray();

        return new AcceptanceTrxReadResult(path, AcceptanceTrxReadStatus.Readable, failures);
    }

    private static string? ResolveTestName(XElement result, XElement? definition)
    {
        var testName = result.Attribute("testName")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(testName))
        {
            return testName;
        }

        var testMethod = definition?.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("TestMethod", StringComparison.Ordinal));
        var className = testMethod?.Attribute("className")?.Value?.Trim();
        var methodName = testMethod?.Attribute("name")?.Value?.Trim();
        return !string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(methodName)
            ? $"{className}.{methodName}"
            : null;
    }

    private static string? ElementValue(XElement? parent, string localName) =>
        parent?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.Ordinal))
            ?.Value;

    private static string? FirstNonEmptyLine(string? value) =>
        value?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
}
