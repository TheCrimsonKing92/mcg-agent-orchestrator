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
    string Reason);

public static class ModelOutcomeScorecard
{
    public const int DefaultWindowSize = 20;
    public const int MinSamplesForConfidence = 2;

    public static IReadOnlyList<ModelOutcomeRecord> Build(
        IEnumerable<TaskSpec> tasks,
        int windowSize = DefaultWindowSize)
    {
        var qualified = tasks
            .Where(HasDispatchWithModel)
            .Where(task => task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Failed)
            .ToList();

        return qualified
            .GroupBy(task => (
                ProviderName: task.LastDispatch!.ProviderName!,
                ModelName: task.LastDispatch.ModelName!))
            .OrderBy(group => group.Key.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var recent = group
                    .OrderByDescending(task => task.LastDispatch!.DispatchedAt)
                    .Take(windowSize)
                    .ToList();
                return BuildRecord(group.Key.ProviderName, group.Key.ModelName, recent);
            })
            .ToList();
    }

    private static bool HasDispatchWithModel(TaskSpec task)
    {
        return !string.IsNullOrWhiteSpace(task.LastDispatch?.ProviderName) &&
               !string.IsNullOrWhiteSpace(task.LastDispatch.ModelName);
    }

    private static ModelOutcomeRecord BuildRecord(
        string providerName,
        string modelName,
        List<TaskSpec> recentTasks)
    {
        var pairs = recentTasks
            .Select(task => (task, fit: ModelFitEvidence.TryParseNote(ModelFitEvidence.FindLatestNote(task))))
            .ToList();

        var completed = pairs.Count(p => p.task.Status == WorkTaskStatus.Completed);
        var failed = pairs.Count(p => p.task.Status == WorkTaskStatus.Failed);
        var adequate = pairs.Count(p => p.fit?.Fit == "adequate");
        var overkill = pairs.Count(p => p.fit?.Fit == "overkill");
        var underpowered = pairs.Count(p => p.fit?.Fit == "underpowered");
        var divergence = pairs.Count(p => p.fit?.Fit == "adequate" && p.task.Status == WorkTaskStatus.Failed);

        var (recommendation, reason) = BuildRecommendation(
            recentTasks.Count, completed, failed, underpowered, divergence);

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
            reason);
    }

    private static (ModelOutcomeRecommendation Recommendation, string Reason) BuildRecommendation(
        int total,
        int completed,
        int failed,
        int underpowered,
        int divergence)
    {
        if (total < MinSamplesForConfidence)
        {
            return (ModelOutcomeRecommendation.Neutral,
                $"Insufficient samples ({total}/{MinSamplesForConfidence} required) for a confident recommendation.");
        }

        if (failed * 2 >= total)
        {
            var divergenceNote = divergence > 0
                ? $" {divergence} self-rated adequate dispatch(es) failed."
                : string.Empty;
            return (ModelOutcomeRecommendation.Avoid,
                $"{failed}/{total} recent dispatches failed.{divergenceNote}");
        }

        if (completed == total && underpowered == 0)
        {
            return (ModelOutcomeRecommendation.Prefer,
                $"All {total} recent dispatches completed with no underpowered self-ratings.");
        }

        var parts = new List<string>();
        if (failed > 0)
        {
            parts.Add($"{failed}/{total} dispatches failed");
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
