using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record OperatorIntentCoverageIntent(
    string Id, string Verb, string GoalId, string? TaskId, string? PayloadJson,
    string Channel, OperatorActorKind? ActorKind, string Status, DateTimeOffset CreatedAt);

public sealed record OperatorIntentCoverageTrigger(
    string GoalId, string TaskId, string Kind, DateTimeOffset FirstOccurrence, string? IntentId);

public sealed record OperatorIntentCoverageIntentRow(
    string Id, string Verb, string GoalId, string Target, string ClassKey, bool Structural, string Disposition);

public sealed record OperatorIntentCoverageVerbRow(
    string Verb, int Total, int Agreed, int Disagreed, int Silent, int Uncovered, double Coverage);

public sealed record OperatorIntentCoverageClassRow(
    string ClassKey, int Total, int Agreed, int Disagreed, int Silent, int Uncovered,
    double Coverage, int Rejected, int DistinctGoals, bool Structural, bool AutomatableCandidate);

public sealed record OperatorIntentCoverageResult(
    DateTimeOffset Since, DateTimeOffset Until, int MinCases, int HumanIntents,
    int Agreed, int Disagreed, int Silent, int Uncovered, double Coverage,
    IReadOnlyList<OperatorIntentCoverageVerbRow> Verbs,
    IReadOnlyList<OperatorIntentCoverageClassRow> Classes,
    IReadOnlyList<OperatorIntentCoverageIntentRow> Intents);

/// <summary>Scores recorded human intents against prior typed detector observations.</summary>
public static class OperatorIntentCoverageReport
{
    public static OperatorIntentCoverageResult Build(
        IReadOnlyList<OperatorIntentCoverageIntent> intents,
        IReadOnlyList<OperatorIntentCoverageTrigger> triggers,
        DateTimeOffset since, DateTimeOffset until, int minCases = 30)
    {
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(triggers);
        if (since >= until) throw new ArgumentException("The coverage window must be non-empty.");
        ArgumentOutOfRangeException.ThrowIfLessThan(minCases, 1);

        var parsed = intents.Select(i => new ParsedIntent(i, Payload(i.PayloadJson))).ToArray();
        // The earliest two distinct non-empty shas suffice to answer whether an earlier
        // different sha exists. Build this index from all actors and the whole history.
        var maps = parsed.Where(i => i.Intent.Verb == "criterion-evidence-map")
            .GroupBy(MapKey).ToDictionary(g => g.Key, g => g
                .Where(i => !string.IsNullOrEmpty(Text(i.Payload, "candidateSha")))
                .GroupBy(i => Text(i.Payload, "candidateSha"), StringComparer.Ordinal)
                .Select(s => (Sha: s.Key, At: s.Min(i => i.Intent.CreatedAt)))
                .OrderBy(s => s.At).ThenBy(s => s.Sha, StringComparer.Ordinal).Take(2).ToArray());
        var classified = parsed.Select(i => new ClassifiedIntent(i.Intent,
            i.Intent.TaskId ?? Text(i.Payload, "targetId") ?? "",
            ClassKey(i), Structural(i.Payload))).ToArray();
        var typed = classified.Where(i => i.Intent.Channel is "conductor-steward" or "conductor-author")
            .ToLookup(i => (i.Intent.GoalId, i.Target));
        var silent = triggers.Where(t => string.IsNullOrEmpty(t.IntentId))
            .ToLookup(t => (t.GoalId, t.TaskId));
        var humans = classified.Where(i => (i.Intent.ActorKind is null or OperatorActorKind.Human) &&
            i.Intent.CreatedAt >= since && i.Intent.CreatedAt < until).ToArray();
        var rows = humans.Select(i =>
        {
            var prior = typed[(i.Intent.GoalId, i.Target)]
                .Where(t => t.Intent.CreatedAt <= i.Intent.CreatedAt).ToArray();
            var disposition = prior.Any(t => t.ClassKey == i.ClassKey) ? "agreed"
                : prior.Length > 0 ? "disagreed"
                : !string.IsNullOrEmpty(i.Intent.TaskId) && silent[(i.Intent.GoalId, i.Intent.TaskId)]
                    .Any(t => t.FirstOccurrence <= i.Intent.CreatedAt) ? "silent" : "uncovered";
            return new OperatorIntentCoverageIntentRow(i.Intent.Id, i.Intent.Verb, i.Intent.GoalId,
                i.Target, i.ClassKey, i.Structural, disposition);
        }).ToArray();
        var verbs = rows.GroupBy(i => i.Verb).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var c = Counts(g);
            return new OperatorIntentCoverageVerbRow(g.Key, c.Total, c.Agreed, c.Disagreed,
                c.Silent, c.Uncovered, c.Coverage);
        }).ToArray();
        var rejected = humans.Where(i => i.Intent.Status.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.ClassKey).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var classes = rows.GroupBy(i => i.ClassKey).Select(g =>
        {
            var c = Counts(g);
            var structural = g.All(i => i.Structural);
            return new OperatorIntentCoverageClassRow(g.Key, c.Total, c.Agreed, c.Disagreed,
                c.Silent, c.Uncovered, c.Coverage, rejected.GetValueOrDefault(g.Key),
                g.Select(i => i.GoalId).Distinct(StringComparer.Ordinal).Count(), structural,
                structural && c.Uncovered >= minCases);
        }).OrderByDescending(c => c.Uncovered).ThenBy(c => c.ClassKey, StringComparer.Ordinal).ToArray();
        var totals = Counts(rows);
        return new(since, until, minCases, totals.Total, totals.Agreed, totals.Disagreed,
            totals.Silent, totals.Uncovered, totals.Coverage, verbs, classes, rows);

        string ClassKey(ParsedIntent i)
        {
            var p = i.Payload;
            return i.Intent.Verb switch
            {
                "criterion-evidence-map" => $"criterion-evidence-map scope={Scope(Text(p, "requiredScope"))} binding={(maps[MapKey(i)].Any(s => s.At < i.Intent.CreatedAt && s.Sha != Text(p, "candidateSha")) ? "rebind" : "first")}",
                "criterion-evidence-record" => $"criterion-evidence-record scope={Scope(Text(p, "scope") ?? Text(p, "requiredScope"))} passed={Value(p, "passed")}",
                "retry" => $"retry cause={EnumValue<RetryCause>(p, "retryCause")}",
                "adjudicate" => $"adjudicate shape={Value(p, "shape")} cause={Value(p, "cause")}",
                "verify-manual" => $"verify-manual passed={Value(Property(p, "request"), "passed")}",
                "answer" => $"answer targetKind={EnumValue<OperatorAnswerTargetKind>(p, "targetKind")}",
                _ => i.Intent.Verb
            };
        }
    }

    private static (string Goal, string Criterion, string Scope) MapKey(ParsedIntent i) =>
        (i.Intent.GoalId, Raw(Property(i.Payload, "criterionIndex")), Value(i.Payload, "requiredScope"));

    private static JsonElement Payload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : default;
        }
        catch (JsonException) { return default; }
    }

    private static JsonElement Property(JsonElement p, string key) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var value) ? value : default;
    private static string? Text(JsonElement p, string key) =>
        Property(p, key) is var v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Raw(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "none" : value.GetRawText();
    private static string Value(JsonElement p, string key) => Text(p, key) ?? Raw(Property(p, key));
    private static string Scope(string? scope) => scope is null ? "none"
        : scope.StartsWith("acceptance:", StringComparison.Ordinal) ? "acceptance" : "operator";
    private static string EnumValue<T>(JsonElement p, string key) where T : struct, Enum
    {
        var raw = Value(p, key);
        return Enum.TryParse<T>(raw, out var value) && Enum.IsDefined(value) ||
            Enum.TryParse(raw, true, out value) && Enum.IsDefined(value) ? value.ToString() : raw;
    }

    private static bool Structural(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return true;
        foreach (var property in p.EnumerateObject())
        {
            if (FreeText(property)) return false;
            if (property.Value.ValueKind == JsonValueKind.Object && property.Value.EnumerateObject().Any(FreeText))
                return false;
        }
        return true;
    }

    private static bool FreeText(JsonProperty p) =>
        (p.Name is "text" or "message" or "detail" or "note" or "rule" or "situation" or "rationale") &&
        p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString());

    private static (int Total, int Agreed, int Disagreed, int Silent, int Uncovered, double Coverage)
        Counts(IEnumerable<OperatorIntentCoverageIntentRow> source)
    {
        var rows = source.ToArray();
        var agreed = rows.Count(r => r.Disposition == "agreed");
        return (rows.Length, agreed, rows.Count(r => r.Disposition == "disagreed"),
            rows.Count(r => r.Disposition == "silent"), rows.Count(r => r.Disposition == "uncovered"),
            rows.Length == 0 ? 0 : (double)agreed / rows.Length);
    }

    private sealed record ParsedIntent(OperatorIntentCoverageIntent Intent, JsonElement Payload);
    private sealed record ClassifiedIntent(OperatorIntentCoverageIntent Intent, string Target, string ClassKey, bool Structural);
}
