using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RemoteShadowAgreementSummary
{
    internal sealed record Counts(int Agree, int Disagree, int Incomparable, int Unclassified,
        IReadOnlyDictionary<string, int> IncomparableReasons);

    internal static Counts? Read(string ledgerPath, DateTimeOffset? since)
    {
        var agree = 0;
        var disagree = 0;
        var incomparable = 0;
        var unclassified = 0;
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in SharedJsonlFile.ReadLines(ledgerPath))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("evidence_shadow", out var shadow) || shadow.ValueKind != JsonValueKind.True)
                    continue;
                if (since is { } minimum && (!row.TryGetProperty("observed_at", out var observed) ||
                    observed.ValueKind != JsonValueKind.String || !observed.TryGetDateTimeOffset(out var at) || at < minimum))
                    continue;
                switch (ReadString(row, "agreement"))
                {
                    case "agree": agree++; break;
                    case "disagree": disagree++; break;
                    case "incomparable":
                        incomparable++;
                        var reason = ReadString(row, "agreement_reason") ?? "missing";
                        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                        break;
                    default: unclassified++; break;
                }
            }
            catch (JsonException) { /* The existing report counts unreadable rows. */ }
        }
        return agree + disagree + incomparable + unclassified == 0
            ? null : new(agree, disagree, incomparable, unclassified, reasons);
    }

    internal static string Format(Counts counts) => FormattableString.Invariant(
        $"shadow_agreement\tagree={counts.Agree}\tdisagree={counts.Disagree}\tincomparable={counts.Incomparable}\tunclassified={counts.Unclassified}\tincomparable_reasons={JsonSerializer.Serialize(counts.IncomparableReasons)}");

    private static string? ReadString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
