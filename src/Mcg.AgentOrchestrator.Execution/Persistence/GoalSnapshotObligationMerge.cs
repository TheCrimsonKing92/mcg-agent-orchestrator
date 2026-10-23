using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalSnapshotObligationMerge
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static (IReadOnlyList<CriterionEvidenceObligation>? Obligations, IReadOnlyList<string> ConflictIds) Merge(
        IReadOnlyList<CriterionEvidenceObligation>? baseline,
        IReadOnlyList<CriterionEvidenceObligation>? stored,
        IReadOnlyList<CriterionEvidenceObligation>? current)
    {
        // Preserve the exact stored representation when the tick has no delta.
        if (EqualsJson(current, baseline)) return (stored, []);

        var baselineById = Index(baseline);
        var storedById = Index(stored);
        var currentById = Index(current);
        var merged = new List<CriterionEvidenceObligation>();
        var conflicts = new List<string>();
        var ids = (stored ?? []).Concat(current ?? []).Concat(baseline ?? [])
            .Select(item => item.Id).Distinct(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            baselineById.TryGetValue(id, out var before);
            storedById.TryGetValue(id, out var saved);
            currentById.TryGetValue(id, out var tick);

            CriterionEvidenceObligation? picked;
            if (EqualsJson(saved, before)) picked = tick;
            else if (EqualsJson(tick, before) || EqualsJson(saved, tick)) picked = saved;
            else
            {
                picked = saved;
                conflicts.Add(id);
            }

            if (picked is not null) merged.Add(picked);
        }

        return (merged.Count == 0 ? null : merged.ToArray(), conflicts.ToArray());

        static Dictionary<string, CriterionEvidenceObligation> Index(IReadOnlyList<CriterionEvidenceObligation>? items) =>
            (items ?? []).GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        static bool EqualsJson<T>(T left, T right) =>
            string.Equals(JsonSerializer.Serialize(left, SerializerOptions),
                JsonSerializer.Serialize(right, SerializerOptions), StringComparison.Ordinal);
    }
}
