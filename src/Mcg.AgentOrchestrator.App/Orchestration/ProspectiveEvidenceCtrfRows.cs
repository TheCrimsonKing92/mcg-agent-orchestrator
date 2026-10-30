using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProspectiveEvidenceCtrfRows
{
    internal static IReadOnlySet<string> FileExtensions(IEnumerable<ProspectiveEvidenceTrxRows.Row> rows) =>
        rows.Select(row => row.FileIdentity).OfType<string>().Select(Path.GetExtension)
            .Where(extension => extension.Length > 0).ToHashSet(StringComparer.Ordinal);

    internal static bool MatchesSuite(string selector, ProspectiveEvidenceTrxRows.Row row,
        IReadOnlySet<string> extensions, IReadOnlyList<string> variants) =>
        row.FileIdentity is not null &&
        CrosswalkSelectorResolution.MatchesSuite(selector, row.FullIdentity, extensions, variants);

    internal static IReadOnlyList<ProspectiveEvidenceTrxRows.Row>? TryRead(string path)
    {
        using var reader = File.OpenText(path);
        if (!path.EndsWith(".ctrf.json", StringComparison.OrdinalIgnoreCase))
        {
            while (reader.Peek() is var character && character >= 0 && char.IsWhiteSpace((char)character))
                reader.Read();
            if (reader.Peek() != '{') return null;
        }

        using var document = JsonDocument.Parse(reader.ReadToEnd());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object ||
            !results.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("CTRF result must contain a results.tests array.");

        var rows = new List<ProspectiveEvidenceTrxRows.Row>();
        foreach (var test in tests.EnumerateArray())
        {
            var testId = ReadString(test, "testId");
            if (testId.Length == 0) throw new InvalidDataException("CTRF testId must be nonempty.");
            var name = ReadString(test, "name");
            var status = ReadString(test, "status");
            var outcome = status switch
            {
                "passed" => "Passed",
                "failed" => "Failed",
                _ => "NotExecuted"
            };
            var delimiter = testId.IndexOf("::", StringComparison.Ordinal);
            rows.Add(new(testId, name, null, outcome, path,
                FileIdentity: delimiter >= 0 ? testId[..delimiter].Replace('\\', '/') : null));
        }

        // Validate every test before exposing rows; malformed results cannot supply partial evidence.
        return rows;
    }

    private static string ReadString(JsonElement test, string property)
    {
        if (test.ValueKind != JsonValueKind.Object ||
            !test.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"CTRF test must contain a string {property}.");
        return value.GetString()!;
    }
}
