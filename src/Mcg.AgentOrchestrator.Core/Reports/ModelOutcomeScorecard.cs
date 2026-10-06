namespace Mcg.AgentOrchestrator.Core;

public enum ModelOutcomeRecommendation
{
    Prefer,
    Neutral,
    Avoid
}

public sealed record ModelOutcomeRecord(
    string ProviderName,
    string ModelName,
    int Completed,
    int Failed,
    int SelfRatedAdequate,
    int SelfRatedOverkill,
    int SelfRatedUnderpowered,
    int Divergence,
    ModelOutcomeRecommendation Recommendation,
    string Reason,
    int RealFailures = 0,
    int EnvironmentalFailures = 0,
    int ManufacturedFixedFailures = 0,
    int UnknownEraFailures = 0,
    string? DispatchLane = null,
    int ClassMismatchFailures = 0,
    string ClassMismatchRules = "",
    bool IsBound = true)
{
    public int NonRealFailures => EnvironmentalFailures + ManufacturedFixedFailures + UnknownEraFailures;
}

public static class ModelOutcomeScorecard
{
    public const int DefaultWindowSize = 20;
    public const int MinSamplesForConfidence = 2;

    public static IReadOnlyList<ModelOutcomeRecord> Build(
        IEnumerable<ModelFitHistoryRow> rows,
        BoundModelSet boundModels,
        int windowSize = DefaultWindowSize) =>
        ApplyBoundModels(Build(rows, windowSize), boundModels);

    public static IReadOnlyList<ModelOutcomeRecord> ApplyBoundModels(
        IReadOnlyList<ModelOutcomeRecord> records,
        BoundModelSet? boundModels) =>
        records.Select(record => record with
        {
            IsBound = boundModels is not { IsAvailable: true } || boundModels.Contains(record.ModelName)
        }).ToList();

    public static IReadOnlyList<ModelOutcomeRecord> Build(
        IEnumerable<Goal> goals,
        int windowSize = DefaultWindowSize)
    {
        return Build(ModelFitHistory.FromGoals(goals), windowSize);
    }

    public static IReadOnlyList<ModelOutcomeRecord> Build(
        IEnumerable<ModelFitHistoryRow> rows,
        int windowSize = DefaultWindowSize)
    {
        var qualified = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProviderName))
            .Where(row => !string.IsNullOrWhiteSpace(row.ModelName))
            .Where(row => row.Outcome is WorkTaskStatus.Completed or WorkTaskStatus.Failed)
            .ToList();

        return qualified
            .GroupBy(row => (
                row.ProviderName,
                row.ModelName,
                DispatchLane: LunaLaneNames.NormalizeDispatchLane(row.DispatchLane)))
            .OrderBy(group => group.Key.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.DispatchLane, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var recent = group
                    .OrderByDescending(row => row.Timestamp)
                    .Take(windowSize)
                    .ToList();
                return BuildRecord(group.Key.ProviderName, group.Key.ModelName, group.Key.DispatchLane, recent);
            })
            .ToList();
    }

    private static ModelOutcomeRecord BuildRecord(
        string providerName,
        string modelName,
        string? dispatchLane,
        List<ModelFitHistoryRow> recentRows)
    {
        var n = recentRows.Count;
        // Linear recency weights: index 0 (newest) gets weight n, index n-1 (oldest) gets 1.
        var pairs = recentRows
            .Select((row, i) => (
                row,
                weight: n - i))
            .ToList();

        var completed = pairs.Count(p => p.row.IsCompleted);
        var failed = pairs.Count(p => p.row.IsFailed);
        var adequate = pairs.Count(p => p.row.SelfRating == ModelFitHistory.Adequate);
        var overkill = pairs.Count(p => p.row.SelfRating == ModelFitHistory.Overkill);
        var underpowered = pairs.Count(p => p.row.SelfRating == ModelFitHistory.Underpowered);
        var realFailures = pairs.Count(p => p.row.IsFailed && p.row.OutcomeClass == TaskOutcomeClass.RealFailure);
        var environmentalFailures = pairs.Count(p => p.row.IsFailed && p.row.OutcomeClass == TaskOutcomeClass.Environmental);
        var manufacturedFailures = pairs.Count(p => p.row.IsFailed && p.row.OutcomeClass == TaskOutcomeClass.ManufacturedFixed);
        var unknownEraFailures = pairs.Count(p => p.row.IsFailed && p.row.OutcomeClass == TaskOutcomeClass.UnknownEra);
        var classMismatchRows = recentRows.Where(row => row.IsFailed &&
            row.OutcomeClass is TaskOutcomeClass.Success or TaskOutcomeClass.ReconciledToSuccess).ToList();
        var classMismatchRules = string.Join(",", classMismatchRows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.OutcomeRule) ? "none" : row.OutcomeRule.ToLowerInvariant())
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}"));
        var divergence = pairs.Count(p => p.row.SelfRating is ModelFitHistory.Divergence ||
            (p.row.SelfRating == ModelFitHistory.Adequate && p.row.IsFailed && p.row.OutcomeClass == TaskOutcomeClass.RealFailure));

        var totalWeight = pairs.Sum(p => p.weight);
        var weightedFailed = pairs.Where(p => p.row.IsFailed && p.row.OutcomeClass == TaskOutcomeClass.RealFailure).Sum(p => p.weight);
        var weightedCompleted = pairs.Where(p => p.row.IsCompleted).Sum(p => p.weight);
        var weightedUnderpowered = pairs.Where(p => p.row.SelfRating == ModelFitHistory.Underpowered).Sum(p => p.weight);

        var (recommendation, reason) = BuildRecommendation(
            n, completed, failed, realFailures, environmentalFailures, manufacturedFailures, unknownEraFailures, underpowered, divergence,
            totalWeight, weightedFailed, weightedCompleted, weightedUnderpowered);

        return new ModelOutcomeRecord(
            providerName,
            modelName,
            completed,
            failed,
            adequate,
            overkill,
            underpowered,
            divergence,
            recommendation,
            reason,
            realFailures,
            environmentalFailures,
            manufacturedFailures,
            unknownEraFailures,
            dispatchLane,
            classMismatchRows.Count,
            classMismatchRules);
    }

    private static (ModelOutcomeRecommendation Recommendation, string Reason) BuildRecommendation(
        int total,
        int completed,
        int failed,
        int realFailures,
        int environmentalFailures,
        int manufacturedFailures,
        int unknownEraFailures,
        int underpowered,
        int divergence,
        int totalWeight,
        int weightedFailed,
        int weightedCompleted,
        int weightedUnderpowered)
    {
        if (total < MinSamplesForConfidence)
        {
            return (ModelOutcomeRecommendation.Neutral,
                $"Insufficient samples ({total}/{MinSamplesForConfidence} required) for a confident recommendation.");
        }

        if (weightedFailed * 2 >= totalWeight)
        {
            var divergenceNote = divergence > 0
                ? $" {divergence} self-rated adequate dispatch(es) failed."
                : string.Empty;
            return (ModelOutcomeRecommendation.Avoid,
                $"{realFailures}/{total} recent dispatches failed for real/code reasons; " +
                $"{environmentalFailures} environmental, {manufacturedFailures} manufactured-fixed, {unknownEraFailures} unknown-era failure(s) excluded from avoidance.{divergenceNote}");
        }

        if (weightedCompleted == totalWeight && weightedUnderpowered == 0)
        {
            return (ModelOutcomeRecommendation.Prefer,
                $"All {total} recent dispatches completed with no underpowered self-ratings.");
        }

        var parts = new List<string>();
        if (realFailures > 0)
        {
            parts.Add($"{realFailures}/{total} real/code failure(s)");
        }

        if (environmentalFailures > 0)
        {
            parts.Add($"{environmentalFailures} environmental failure(s)");
        }

        if (manufacturedFailures > 0)
        {
            parts.Add($"{manufacturedFailures} manufactured-fixed failure(s)");
        }

        if (unknownEraFailures > 0)
        {
            parts.Add($"{unknownEraFailures} unknown-era failure(s)");
        }

        if (underpowered > 0)
        {
            parts.Add($"{underpowered} underpowered self-rating(s)");
        }

        if (divergence > 0)
        {
            parts.Add($"{divergence} adequate-but-failed divergence(s)");
        }

        var detail = parts.Count > 0 ? ": " + string.Join("; ", parts) : string.Empty;
        return (ModelOutcomeRecommendation.Neutral, $"Mixed outcomes{detail}.");
    }
}
