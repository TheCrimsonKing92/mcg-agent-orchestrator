using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only the declared-test check's own CTRF can authorize declared-test selectors.
internal static class DeclaredTestReportRows
{
    internal sealed record Row(string Identity, string Name, IReadOnlyList<string> Suite,
        string Outcome, string Path);

    internal sealed record Report(bool Applies, IReadOnlyList<Row> Rows);

    internal static bool IsDeclaredCheck(GoalAcceptanceVerifier.AcceptanceManifestCheck check) =>
        check.Name == DeclaredProjectDiscoveryAdapter.TestCheckName &&
        string.Equals(check.Type, "junit-command", StringComparison.OrdinalIgnoreCase);

    internal static Report Read(IReadOnlyList<string> paths, string directory,
        ProspectiveEvidenceGateResolver.GateCheckOutcomes? checks = null)
    {
        var declared = checks?.Declared.Any(IsDeclaredCheck) == true;
        if (checks is not null && !declared) return new(false, []);
        var rows = new List<Row>();
        try
        {
            foreach (var relative in paths)
            {
                var path = Path.GetFullPath(relative, directory);
                var validated = ProspectiveEvidenceCtrfRows.TryRead(path);
                if (validated is null) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var results = document.RootElement.GetProperty("results");
                if (!results.TryGetProperty("tool", out var tool) ||
                    !tool.TryGetProperty("name", out var name) ||
                    name.GetString() != DeclaredProjectDiscoveryAdapter.TestCheckName) continue;
                declared = true;
                var tests = results.GetProperty("tests").EnumerateArray().ToArray();
                for (var index = 0; index < tests.Length; index++)
                {
                    var suite = tests[index].TryGetProperty("suite", out var value)
                        ? value.EnumerateArray().Select(segment => segment.GetString() ??
                            throw new InvalidDataException("CTRF suite segments must be strings.")).ToArray()
                        : [];
                    var row = validated[index];
                    rows.Add(new(row.FullIdentity, row.ShortIdentity, suite, row.Outcome!, path));
                }
            }
            return new(declared, rows);
        }
        catch (Exception)
        {
            // Never expose a readable subset of an incomplete source.
            return new(declared, []);
        }
    }
}
