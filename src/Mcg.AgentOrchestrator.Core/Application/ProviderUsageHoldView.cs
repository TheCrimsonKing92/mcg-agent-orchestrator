namespace Mcg.AgentOrchestrator.Core;

public sealed record ProviderUsageHoldRow(ProviderBudgetExhaustionHold Hold, ProviderBudgetHoldSchedule? Schedule);

/// <summary>Read-only operator projection of held dispatched bindings and their probe schedules.</summary>
public static class ProviderUsageHoldView
{
    public static IReadOnlyList<ProviderUsageHoldRow> Read(IReadOnlyCollection<Goal> goals, DateTimeOffset now) =>
        Read(goals, goals, now);

    internal static IReadOnlyList<ProviderUsageHoldRow> Read(IReadOnlyCollection<Goal> holdScope,
        IReadOnlyCollection<Goal> scheduleScope, DateTimeOffset now)
    {
        var bindings = holdScope.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName,
                Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
        var holds = new Dictionary<string, ProviderBudgetExhaustionHold>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
            if (DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(holdScope,
                    binding.Provider, binding.Credential, out var hold))
                holds.TryAdd(hold.BindingKey, hold);
        return holds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            ProviderBudgetHoldSchedule? schedule;
            try { schedule = ProviderBudgetHoldSchedules.Evaluate(scheduleScope, pair.Value, now); }
            catch (InvalidOperationException) { schedule = null; }
            return new ProviderUsageHoldRow(pair.Value, schedule);
        }).ToArray();
    }

    public static string FormatStatusLine(ProviderUsageHoldRow row)
    {
        var source = $"source={row.Hold.SourceGoalId.Value[..8]}/{row.Hold.SourceTaskId.Value[..8]}";
        return row.Schedule is { } schedule
            ? $"  {row.Hold.BindingKey} | attempt={schedule.Attempt} | next_probe={ProviderBudgetHoldSchedules.FormatInstant(schedule.NextProbeAt)} | reset_source={schedule.ResetSource} | last_probe={schedule.LastProbeOutcome} | {source}"
            : $"  {row.Hold.BindingKey} | schedule=unavailable | {source}";
    }
}
