using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A complete, bounded view of passed method durations; partial TRX reads are never evidence.
internal sealed record LaneTestDurationSnapshot(IReadOnlyDictionary<string, double> Tests, string? UnavailableReason = null)
{
    internal const int MaximumTests = 150;

    internal static LaneTestDurationSnapshot Read(IReadOnlyList<string>? paths)
    {
        var currentPath = string.Empty;
        try
        {
            if (paths is null || paths.Count == 0) return Empty("no-trx-paths");
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                currentPath = path;
                if (!File.Exists(path)) return Empty("trx-missing:" + path);
                var document = XDocument.Load(path);
                var definitions = document.Descendants().Where(e => e.Name.LocalName == "UnitTest")
                    .GroupBy(e => (string?)e.Attribute("id") ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var fileTotals = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var row in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
                {
                    if (!string.Equals((string?)row.Attribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase)) continue;
                    var raw = (string?)row.Attribute("duration");
                    if (raw is null) return Empty("duration-missing:" + path);
                    if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var duration) || duration < TimeSpan.Zero)
                        return Empty("duration-invalid:" + path);
                    definitions.TryGetValue((string?)row.Attribute("testId") ?? "", out var definition);
                    var resolved = AcceptanceTrxTestIdentityResolver.Resolve(row, definition);
                    if (string.IsNullOrWhiteSpace(resolved)) return Empty("identity-missing:" + path);
                    var identity = AcceptanceTrxTestIdentityResolver.NormalizeSelector(resolved);
                    fileTotals[identity] = fileTotals.GetValueOrDefault(identity) + duration.TotalMilliseconds;
                }
                foreach (var (identity, milliseconds) in fileTotals)
                    totals[identity] = Math.Max(totals.GetValueOrDefault(identity), milliseconds);
            }
            var tests = Bound(totals);
            return new(tests, tests.Count == 0 ? "no-passed-tests-above-floor" : null);
        }
        catch (XmlException) { return Empty("trx-malformed:" + currentPath); }
        catch (Exception error) { return Empty("trx-unreadable:" + currentPath + ":" + error.GetType().Name); }
    }

    internal static Dictionary<string, double> Bound(IReadOnlyDictionary<string, double> tests) => tests
        .Where(pair => double.IsFinite(pair.Value) && pair.Value >= 1000)
        .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
        .Take(MaximumTests).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static LaneTestDurationSnapshot Empty(string reason) => new(new Dictionary<string, double>(), reason);
}
