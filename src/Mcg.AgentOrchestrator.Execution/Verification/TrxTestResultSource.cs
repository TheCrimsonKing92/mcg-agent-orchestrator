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
        var rows = document.Descendants()
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
                var record = new TestResult(id, result.Attribute("testName")?.Value ?? id, null,
                    className is null ? Array.Empty<string>() : new[] { className },
                    Status(result.Attribute("outcome")?.Value), Duration(result.Attribute("duration")?.Value),
                    ElementValue(errorInfo, "Message"), ElementValue(errorInfo, "StackTrace"),
                    Array.Empty<TestResultAttempt>(), null, null, ReportedBy);
                return (Record: record, NativeId: result.Attribute("testId")?.Value);
            }).ToArray();
        return new(reportPath, TestResultReadStatus.Readable, Disambiguate(rows), null);
    }

    private static TestResult[] Disambiguate((TestResult Record, string? NativeId)[] rows)
    {
        var collisions = rows.GroupBy(row => row.Record.Id, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrEmpty(group.Key) && group.Count() > 1 &&
                group.All(row => !string.IsNullOrEmpty(row.NativeId)) &&
                group.Select(row => row.NativeId).Distinct(StringComparer.Ordinal).Count() == group.Count())
            .SelectMany(group => group).ToArray();
        if (collisions.Length == 0) return rows.Select(row => row.Record).ToArray();

        var originalIds = rows.Select(row => row.Record.Id).ToHashSet(StringComparer.Ordinal);
        // This namespace belongs to one conversion, not a run-independent native identity registry.
        // Choose it against ALL original IDs, including rows in ineligible collision groups.
        for (var discriminator = 0; ; discriminator = checked(discriminator + 1))
        {
            var replacements = collisions.ToDictionary(row => (row.Record.Id, row.NativeId!),
                row => EncodeIdentity(discriminator, row.Record.Id, row.NativeId!));
            if (replacements.Values.Distinct(StringComparer.Ordinal).Count() != collisions.Length)
                throw new InvalidOperationException("TRX normalized identity encoding is not injective.");
            if (replacements.Values.Any(originalIds.Contains)) continue;
            return rows.Select(row => replacements.TryGetValue((row.Record.Id, row.NativeId ?? ""), out var id)
                ? row.Record with { Id = id } : row.Record).ToArray();
        }
    }

    // Length framing preserves opaque XML-decoded strings, including delimiters and Unicode.
    private static string EncodeIdentity(int discriminator, string id, string nativeId) =>
        FormattableString.Invariant($"trx-native:v1:{discriminator}:{id.Length}:{id}{nativeId.Length}:{nativeId}");

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
