using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class TrxTestResultSource : ITestResultSource
{
    public string ReportedBy => "trx";

    public TestResultReadResult Read(string reportPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(reportPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failure(reportPath, TestResultReadStatus.Missing, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(reportPath, TestResultReadStatus.Unreadable, ex);
        }
        catch (XmlException ex)
        {
            return Failure(reportPath, TestResultReadStatus.Unparseable, ex);
        }

        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest" &&
                !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var records = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult")
            .Select(result =>
            {
                definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
                var id = AcceptanceTrxTestIdentityResolver.Resolve(result, definition)
                    ?? result.Attribute("testId")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(id)) id = "unknown test";
                var className = definition?.Descendants().FirstOrDefault(e => e.Name.LocalName == "TestMethod")
                    ?.Attribute("className")?.Value;
                var errorInfo = result.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
                return new TestResult(id, result.Attribute("testName")?.Value ?? id, null,
                    className is null ? Array.Empty<string>() : new[] { className },
                    Status(result.Attribute("outcome")?.Value), Duration(result.Attribute("duration")?.Value),
                    ElementValue(errorInfo, "Message"), ElementValue(errorInfo, "StackTrace"),
                    Array.Empty<TestResultAttempt>(), null, null, ReportedBy);
            }).ToArray();
        return new(reportPath, TestResultReadStatus.Readable, records, null);
    }

    private static TestResultStatus Status(string? outcome)
    {
        outcome = outcome?.Trim();
        if (string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase)) return TestResultStatus.Passed;
        if (AcceptanceTrxOutcomeTaxonomy.IsFatal(outcome))
            return string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)
                ? TestResultStatus.Failed : TestResultStatus.Error;
        return string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outcome, "Inconclusive", StringComparison.OrdinalIgnoreCase)
            ? TestResultStatus.Skipped : TestResultStatus.Error;
    }

    private static long? Duration(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)
            ? (long)Math.Round(duration.Ticks / (decimal)TimeSpan.TicksPerMillisecond, MidpointRounding.AwayFromZero)
            : null;

    private static string? ElementValue(XElement? parent, string name) =>
        parent?.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    private static TestResultReadResult Failure(string path, TestResultReadStatus status, Exception ex) =>
        new(path, status, Array.Empty<TestResult>(), AcceptanceTrxOutcomeTaxonomy.FirstNonEmptyLine(ex.Message));
}
