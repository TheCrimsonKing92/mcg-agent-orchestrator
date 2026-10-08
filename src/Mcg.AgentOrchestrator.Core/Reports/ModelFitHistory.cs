namespace Mcg.AgentOrchestrator.Core;

public sealed record ModelFitHistoryRow(
    string GoalId,
    string TaskId,
    AgentRole Role,
    string ProviderName,
    string ModelName,
    TaskComplexity? Complexity,
    string? TaskShape,
    WorkTaskStatus Outcome,
    string SelfRating,
    DateTimeOffset Timestamp,
    string? OutcomeRule = null,
    TaskOutcomeClass OutcomeClass = TaskOutcomeClass.UnknownEra,
    string? DispatchLane = null)
{
    public bool IsCompleted => Outcome == WorkTaskStatus.Completed;
    public bool IsFailed => Outcome == WorkTaskStatus.Failed;
}

public sealed record ModelFitBestFit(
    AgentRole Role,
    string ProviderName,
    string ModelName,
    ModelOutcomeRecommendation Recommendation,
    string Reason);

public static class ModelFitHistory
{
    public const string Adequate = "adequate";
    public const string Overkill = "overkill";
    public const string Underpowered = "underpowered";
    public const string Divergence = "divergence";
    public const string Unknown = "unknown";

    public static ModelFitHistoryRow? TryCreateRow(Goal goal, TaskSpec task)
    {
        if (task.LastDispatch is not { ProviderName: { Length: > 0 } providerName, ModelName: { Length: > 0 } modelName } dispatch)
        {
            return null;
        }

        var observation = ModelFitEvidence.TryParseNote(ModelFitEvidence.FindLatestNote(task));
        var selfRating = NormalizeSelfRating(observation?.Fit);
        var outcome = TaskOutcomeClassifier.FromTimeline(
            goal.Timeline,
            task.Id,
            task.Status,
            task.Status == WorkTaskStatus.Failed ? null : task.LastVerification?.CompletedAt);

        if (task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Failed) &&
            !(task.Status == WorkTaskStatus.WaitingForHuman && outcome.Class == TaskOutcomeClass.Finding))
        {
            return null;
        }

        return new ModelFitHistoryRow(
            goal.Id.Value,
            task.Id.Value,
            task.RequiredRole,
            providerName,
            modelName,
            dispatch.TaskComplexity,
            observation?.TaskShape,
            task.Status,
            selfRating,
            task.LastVerification?.CompletedAt ?? dispatch.DispatchedAt,
            outcome.Rule,
            outcome.Class,
            dispatch.DispatchLane ?? dispatch.WorkerName);
    }

    public static IReadOnlyList<ModelFitHistoryRow> FromGoals(IEnumerable<Goal> goals)
    {
        return goals
            .SelectMany(goal => goal.Tasks.Select(task => TryCreateRow(goal, task)))
            .Where(row => row is not null)
            .Cast<ModelFitHistoryRow>()
            .ToList();
    }

    public static ModelFitBestFit? QueryBestFitForRole(
        IEnumerable<ModelFitHistoryRow> rows,
        AgentRole role,
        int windowSize = ModelOutcomeScorecard.DefaultWindowSize)
    {
        return ModelOutcomeScorecard.Build(rows.Where(row => row.Role == role), windowSize)
            .Where(record => record.Recommendation != ModelOutcomeRecommendation.Avoid)
            .Where(record => record.SelfRatedUnderpowered == 0)
            .Where(record => record.Completed > record.RealFailures)
            .OrderBy(record => record.Recommendation == ModelOutcomeRecommendation.Prefer ? 0 : 1)
            .ThenByDescending(record => record.Completed)
            .ThenByDescending(record => record.SelfRatedAdequate)
            .ThenBy(record => record.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.ModelName, StringComparer.OrdinalIgnoreCase)
            .Select(record => new ModelFitBestFit(role, record.ProviderName, record.ModelName, record.Recommendation, record.Reason))
            .FirstOrDefault();
    }

    public static string NormalizeSelfRating(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Adequate => Adequate,
            Overkill => Overkill,
            Underpowered => Underpowered,
            Divergence => Divergence,
            _ => Unknown
        };
    }
}
