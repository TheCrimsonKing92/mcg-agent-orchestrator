using System.Collections.Immutable;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class NormalizedTestResultSource : ITestResultSource
{
    internal const string SchemaVersion = "mcg-test-result/1";
    internal static readonly ImmutableArray<string> StatusValues =
        ImmutableArray.Create(new[] { "passed", "failed", "skipped", "error" });
    internal static readonly ImmutableArray<string> TestPropertyNames = ImmutableArray.Create(new[]
    {
        "testId", "name", "status", "duration", "filePath", "suite", "message", "trace", "retryAttempts", "extra"
    });

    public string ReportedBy => "normalized";

    public TestResultReadResult Read(string reportPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (ValidateReport(document.RootElement) is { } pointer)
                return new(reportPath, TestResultReadStatus.Invalid, Array.Empty<TestResult>(), pointer);

            var results = document.RootElement.GetProperty("results");
            var tool = results.GetProperty("tool").GetProperty("name").GetString()!;
            var records = results.GetProperty("tests").EnumerateArray().Select(test => ReadTest(test, tool)).ToArray();
            return new(reportPath, TestResultReadStatus.Readable, records, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failure(reportPath, TestResultReadStatus.Missing, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(reportPath, TestResultReadStatus.Unreadable, ex);
        }
        catch (JsonException ex)
        {
            return Failure(reportPath, TestResultReadStatus.Unparseable, ex);
        }
    }

    private static string? ValidateReport(JsonElement root)
    {
        var violation = RequiredObject(root, "", "reportFormat", "schemaVersion", "results");
        if (violation is not null) return violation;
        if (!IsString(root.GetProperty("reportFormat"), "CTRF")) return "/reportFormat";
        if (!IsString(root.GetProperty("schemaVersion"), SchemaVersion)) return "/schemaVersion";
        var results = root.GetProperty("results");
        violation = RequiredObject(results, "/results", "tool", "tests");
        if (violation is not null) return violation;
        var tool = results.GetProperty("tool");
        violation = RequiredObject(tool, "/results/tool", "name");
        if (violation is not null) return violation;
        if (!NonEmptyString(tool.GetProperty("name"))) return "/results/tool/name";
        var tests = results.GetProperty("tests");
        if (tests.ValueKind != JsonValueKind.Array) return "/results/tests";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var test in tests.EnumerateArray())
        {
            violation = ValidateTest(test, $"/results/tests/{index++}", ids);
            if (violation is not null) return violation;
        }
        return null;
    }

    private static string? ValidateTest(JsonElement test, string path, HashSet<string> ids)
    {
        var violation = RequiredObject(test, path, "testId", "name", "status");
        if (violation is not null) return violation;
        foreach (var name in TestPropertyNames)
        {
            if (!test.TryGetProperty(name, out var value)) continue;
            var pointer = path + "/" + name;
            switch (name)
            {
                case "testId":
                    if (!NonEmptyString(value) || !ids.Add(value.GetString()!)) return pointer;
                    break;
                case "name":
                    if (!NonEmptyString(value)) return pointer;
                    break;
                case "status":
                    if (!ValidStatus(value)) return pointer;
                    break;
                case "duration":
                    if (!TryDuration(value, out _)) return pointer;
                    break;
                case "suite":
                    if (value.ValueKind != JsonValueKind.Array) return pointer;
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) return $"{pointer}/{index}";
                        index++;
                    }
                    break;
                case "retryAttempts":
                    if (value.ValueKind != JsonValueKind.Array) return pointer;
                    var attemptIndex = 0;
                    foreach (var attempt in value.EnumerateArray())
                    {
                        violation = ValidateAttempt(attempt, $"{pointer}/{attemptIndex++}");
                        if (violation is not null) return violation;
                    }
                    break;
                case "extra":
                    violation = RequiredObject(value, pointer);
                    if (violation is not null) return violation;
                    foreach (var field in new[] { "seed", "outputRef", "reportedBy" })
                        if (value.TryGetProperty(field, out var extra) && extra.ValueKind != JsonValueKind.String)
                            return pointer + "/" + field;
                    break;
                default:
                    if (value.ValueKind != JsonValueKind.String) return pointer;
                    break;
            }
        }
        return null;
    }

    private static string? ValidateAttempt(JsonElement attempt, string path)
    {
        var violation = RequiredObject(attempt, path, "status");
        if (violation is not null) return violation;
        if (!ValidStatus(attempt.GetProperty("status"))) return path + "/status";
        if (attempt.TryGetProperty("duration", out var duration) && !TryDuration(duration, out _))
            return path + "/duration";
        foreach (var name in new[] { "message", "trace" })
            if (attempt.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.String)
                return path + "/" + name;
        return null;
    }

    private static string? RequiredObject(JsonElement value, string path, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) return path;
        foreach (var name in required)
            if (!value.TryGetProperty(name, out _)) return path + "/" + name;
        return null;
    }

    private static bool NonEmptyString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 0;

    private static bool IsString(JsonElement value, string expected) =>
        value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool ValidStatus(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && StatusValues.Contains(value.GetString()!);

    private static bool TryDuration(JsonElement value, out long duration)
    {
        duration = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        // Check the sign before numeric conversion, which can underflow a tiny negative number to zero.
        var text = value.GetRawText();
        if (text[0] == '-' && text.Split('e', 'E')[0].Any(c => c is >= '1' and <= '9')) return false;
        if (value.TryGetDecimal(out var precise))
        {
            var rounded = Math.Round(precise, MidpointRounding.AwayFromZero);
            if (rounded < 0 || rounded > long.MaxValue) return false;
            duration = (long)rounded;
            return true;
        }
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
        var roundedDouble = Math.Round(number, MidpointRounding.AwayFromZero);
        if (roundedDouble < 0 || roundedDouble >= 9223372036854775808d) return false;
        duration = (long)roundedDouble;
        return true;
    }

    private static TestResult ReadTest(JsonElement test, string tool)
    {
        var extra = test.TryGetProperty("extra", out var value) ? value : default;
        var suite = test.TryGetProperty("suite", out value)
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : Array.Empty<string>();
        var attempts = test.TryGetProperty("retryAttempts", out value)
            ? value.EnumerateArray().Select(attempt => new TestResultAttempt(ReadStatus(attempt),
                ReadDuration(attempt), Text(attempt, "message"), Text(attempt, "trace"))).ToArray()
            : Array.Empty<TestResultAttempt>();
        return new(test.GetProperty("testId").GetString()!, test.GetProperty("name").GetString()!,
            Text(test, "filePath"), suite, ReadStatus(test), ReadDuration(test), Text(test, "message"),
            Text(test, "trace"), attempts, Text(extra, "seed"), Text(extra, "outputRef"),
            Text(extra, "reportedBy") ?? tool);
    }

    private static TestResultStatus ReadStatus(JsonElement value) =>
        (TestResultStatus)StatusValues.IndexOf(value.GetProperty("status").GetString()!);

    private static long? ReadDuration(JsonElement value) =>
        value.TryGetProperty("duration", out var property) && TryDuration(property, out var duration) ? duration : null;

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property.GetString() : null;

    private static TestResultReadResult Failure(string path, TestResultReadStatus status, Exception ex) =>
        new(path, status, Array.Empty<TestResult>(), AcceptanceTrxOutcomeTaxonomy.FirstNonEmptyLine(ex.Message));
}
