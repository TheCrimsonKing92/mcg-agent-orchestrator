using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record OwnerDigestFitRow(
    string Role, string Provider, string Model, string TaskClass,
    int Rounds, int FirstPassRounds, int ReworkRounds, double? ReworkRate,
    IReadOnlyDictionary<string, int> ReworkByCauseFamily,
    IReadOnlyDictionary<string, int> StopCauses,
    long InputTokens, long CachedInputTokens, long OutputTokens, int UsageUnreported,
    int GoalsLanded, int DistinctGoals, int ShadowDiffersRounds);

internal static class CliOwnerDigestFit
{
    internal static IReadOnlyList<OwnerDigestFitRow> Aggregate(OwnerDigestResult digest,
        IReadOnlyList<Goal> goals, IReadOnlyList<WorkerRoundRecord> rounds)
    {
        // Match the ledger's stable dispatch ordering; retries need no timestamp join.
        var decisions = goals.SelectMany(goal => goal.Tasks.SelectMany(task =>
                task.DispatchHistory.OrderBy(dispatch => dispatch.DispatchedAt)
                    .Select((dispatch, index) => new
                    {
                        Key = (goal.Id.Value, task.Id.Value, Round: index + 1),
                        dispatch.ShadowDecision
                    })))
            .ToDictionary(item => item.Key, item => item.ShadowDecision);
        var landed = digest.Goals.Select(goal => goal.GoalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rounds.Select(round => new
            {
                Round = round,
                Decision = decisions.GetValueOrDefault((round.GoalId, round.TaskId, round.RoundIndex))
            })
            .GroupBy(item => (Role: item.Round.Role.ToString(),
                Provider: Part(item.Round.ProviderName), Model: Part(item.Round.ModelName),
                Class: DispatchTaskClassifier.WireName(item.Decision?.TaskClass ?? DispatchTaskClass.Unrecorded)))
            .OrderBy(group => group.Key.Role, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Provider, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Model, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Class, StringComparer.Ordinal)
            .Select(group =>
            {
                var records = group.Select(item => item.Round).ToArray();
                var rework = records.Where(round => round.ReworkCause != ReworkCauseFamily.FirstPass).ToArray();
                var goalIds = records.Select(round => round.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return new OwnerDigestFitRow(group.Key.Role, group.Key.Provider, group.Key.Model, group.Key.Class,
                    records.Length, records.Length - rework.Length, rework.Length,
                    records.Length == 0 ? null : (double)rework.Length / records.Length,
                    rework.GroupBy(round => round.ReworkCause).OrderBy(cause => cause.Key)
                        .ToDictionary(cause => cause.Key.ToString(), cause => cause.Count()),
                    records.GroupBy(round => round.StopCause).OrderBy(cause => cause.Key)
                        .ToDictionary(cause => cause.Key.ToString(), cause => cause.Count()),
                    records.Sum(round => round.InputTokens ?? 0),
                    records.Sum(round => round.CachedInputTokens ?? 0),
                    records.Sum(round => round.OutputTokens ?? 0),
                    records.Count(round => !round.UsageReported), goalIds.Count(landed.Contains), goalIds.Length,
                    group.Count(item => item.Decision?.Differs == true));
            }).ToArray();
    }

    internal static void WriteText(TextWriter writer, IReadOnlyList<OwnerDigestFitRow> rows)
    {
        writer.WriteLine("Fit by role, model and task class | Provider/model | Task class | Rounds | First pass | Rework | Rework rate | Rework families | Stop causes | Input | Cached input | Output | Usage unreported | Goals landed/distinct | Shadow differs");
        if (rows.Count == 0) writer.WriteLine("none");
        foreach (var row in rows)
        {
            var rate = row.ReworkRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
            writer.WriteLine(FormattableString.Invariant(
                $"{row.Role} | {row.Provider}/{row.Model} | {row.TaskClass} | {row.Rounds} | {row.FirstPassRounds} | {row.ReworkRounds} | {rate} | {Counts(row.ReworkByCauseFamily)} | {Counts(row.StopCauses)} | {row.InputTokens} | {row.CachedInputTokens} | {row.OutputTokens} | {row.UsageUnreported} | {row.GoalsLanded}/{row.DistinctGoals} | {row.ShadowDiffersRounds}"));
        }
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "none" : string.Join(", ", counts.Select(pair => $"{pair.Key}={pair.Value}"));

    private static string Part(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
