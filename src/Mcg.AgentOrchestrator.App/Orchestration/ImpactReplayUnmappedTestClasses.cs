namespace Mcg.AgentOrchestrator.App.Orchestration;

// Explanatory observations only; these classifications never decide lane selection.
internal sealed record ImpactReplayUnmappedTestClasses(
    IReadOnlyDictionary<string, int> CountsByReason,
    IReadOnlyList<ImpactReplayUnmappedTestClasses.TopClass> TopClasses,
    IReadOnlyList<ImpactReplayUnmappedTestClasses.Occurrence> Occurrences)
{
    internal const string NotScanned = "not-scanned";
    internal const string NoLane = "no-lane";
    internal const string Ambiguous = "ambiguous";

    public sealed record Occurrence(string AttemptId, string Class, string Reason, IReadOnlyList<string> FullNames);
    public sealed record TopClass(string Class, string Reason, int Attempts, IReadOnlyList<string> FullNames);

    internal static ImpactReplayUnmappedTestClasses Empty { get; } = Aggregate([]);

    internal static Occurrence? Classify(string attemptId, string simpleName,
        IReadOnlyList<string> matchedFullNames, bool singleMatchHasLane)
    {
        var reason = matchedFullNames.Count switch
        {
            0 => NotScanned,
            > 1 => Ambiguous,
            _ => singleMatchHasLane ? null : NoLane
        };
        return reason is null ? null : new(attemptId, simpleName, reason,
            matchedFullNames.Order(StringComparer.Ordinal).ToArray());
    }

    internal static ImpactReplayUnmappedTestClasses Aggregate(IEnumerable<Occurrence> occurrences)
    {
        var distinct = occurrences.DistinctBy(item => (item.AttemptId, item.Class)).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [NotScanned] = distinct.Count(item => item.Reason == NotScanned),
            [NoLane] = distinct.Count(item => item.Reason == NoLane),
            [Ambiguous] = distinct.Count(item => item.Reason == Ambiguous)
        };
        var top = distinct.GroupBy(item => (item.Class, item.Reason))
            .Select(group => new TopClass(group.Key.Class, group.Key.Reason,
                group.Select(item => item.AttemptId).Distinct(StringComparer.Ordinal).Count(),
                group.SelectMany(item => item.FullNames).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
            .OrderByDescending(item => item.Attempts)
            .ThenBy(item => item.Class, StringComparer.Ordinal)
            .ThenBy(item => item.Reason, StringComparer.Ordinal)
            .Take(30).ToArray();
        return new(counts, top, distinct);
    }
}
