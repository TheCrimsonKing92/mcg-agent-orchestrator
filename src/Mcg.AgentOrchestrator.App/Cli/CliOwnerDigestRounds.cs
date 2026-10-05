using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record OwnerDigestRoundRow(
    string Key, int Rounds, int Completed, int Failed, int Superseded, int Other,
    long InputTokens, long CachedInputTokens, long OutputTokens, int UsageUnreported);

internal sealed record OwnerDigestRoundsResult(
    IReadOnlyList<OwnerDigestRoundRow> ByRole,
    IReadOnlyList<OwnerDigestRoundRow> ByModel,
    OwnerDigestReworkByCause ReworkByCause,
    IReadOnlyList<OwnerDigestFitRow> FitByRoleModelTaskClass);

internal sealed record OwnerDigestReworkRow(
    string Role, string Family, int Rounds,
    long InputTokens, long CachedInputTokens, long OutputTokens, int UsageUnreported);

internal sealed record OwnerDigestReworkTotal(
    int Rounds, int Unclassified, double? UnclassifiedShare,
    long InputTokens, long CachedInputTokens, long OutputTokens, int UsageUnreported);

internal sealed record OwnerDigestReworkByCause(
    IReadOnlyList<OwnerDigestReworkRow> Rows, OwnerDigestReworkTotal Total);

internal static class CliOwnerDigestRounds
{
    internal static OwnerDigestRoundsResult Aggregate(OwnerDigestResult digest, IEnumerable<Goal> goals) =>
        Aggregate(digest, goals, []);

    internal static OwnerDigestRoundsResult Aggregate(OwnerDigestResult digest, IEnumerable<Goal> goals,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        var goalList = goals.ToArray();
        var rounds = WorkerRoundLedger.FromGoals(goalList, intents)
            .Where(round => round.DispatchedAt >= digest.Since && round.DispatchedAt < digest.Until)
            .ToArray();
        return new OwnerDigestRoundsResult(
            Rows(rounds, round => round.Role.ToString()),
            Rows(rounds, round => $"{Part(round.ProviderName)}/{Part(round.ModelName)}"),
            Rework(rounds), CliOwnerDigestFit.Aggregate(digest, goalList, rounds));
    }

    internal static void WriteText(TextWriter writer, OwnerDigestResult digest, IEnumerable<Goal> goals,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        var result = Aggregate(digest, goals, intents);
        CliOwnerDigestFit.WriteText(writer, result.FitByRoleModelTaskClass);
        WriteRows(writer, "Rounds by role", result.ByRole);
        WriteRows(writer, "Rounds by model", result.ByModel);
        writer.WriteLine("Rework rounds by cause | Family | Rounds | Input | Cached input | Output | Usage unreported");
        foreach (var row in result.ReworkByCause.Rows)
            writer.WriteLine(FormattableString.Invariant(
                $"{row.Role} | {row.Family} | {row.Rounds} | {row.InputTokens} | {row.CachedInputTokens} | {row.OutputTokens} | {row.UsageUnreported}"));
        var total = result.ReworkByCause.Total;
        var share = total.UnclassifiedShare?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
        writer.WriteLine(FormattableString.Invariant(
            $"Total | all | {total.Rounds} | {total.InputTokens} | {total.CachedInputTokens} | {total.OutputTokens} | {total.UsageUnreported} | unclassified={total.Unclassified} share={share}"));
    }

    internal static void WriteJson(TextWriter writer, OwnerDigestResult digest, IEnumerable<Goal> goals,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var root = JsonSerializer.SerializeToNode(digest, options)!.AsObject();
        CliOwnerDigestCommand.AddEscapeSources(root, digest);
        root["rounds"] = JsonSerializer.SerializeToNode(Aggregate(digest, goals, intents), options);
        writer.WriteLine(root.ToJsonString(options));
    }

    private static OwnerDigestReworkByCause Rework(IReadOnlyList<WorkerRoundRecord> rounds)
    {
        var rework = rounds.Where(round => round.ReworkCause != ReworkCauseFamily.FirstPass).ToArray();
        var rows = rework.GroupBy(round => (Role: round.Role.ToString(), Family: round.ReworkCause.ToString()))
            .OrderBy(group => group.Key.Role, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Family, StringComparer.Ordinal)
            .Select(group => new OwnerDigestReworkRow(group.Key.Role, group.Key.Family, group.Count(),
                group.Sum(round => round.InputTokens ?? 0),
                group.Sum(round => round.CachedInputTokens ?? 0),
                group.Sum(round => round.OutputTokens ?? 0),
                group.Count(round => !round.UsageReported))).ToArray();
        var unclassified = rework.Count(round => round.ReworkCause == ReworkCauseFamily.Unclassified);
        return new OwnerDigestReworkByCause(rows, new OwnerDigestReworkTotal(rework.Length, unclassified,
            rework.Length == 0 ? null : (double)unclassified / rework.Length,
            rework.Sum(round => round.InputTokens ?? 0),
            rework.Sum(round => round.CachedInputTokens ?? 0),
            rework.Sum(round => round.OutputTokens ?? 0),
            rework.Count(round => !round.UsageReported)));
    }

    private static IReadOnlyList<OwnerDigestRoundRow> Rows(
        IReadOnlyList<WorkerRoundRecord> rounds, Func<WorkerRoundRecord, string> key) =>
        rounds.GroupBy(key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new OwnerDigestRoundRow(group.Key, group.Count(),
                group.Count(round => round.StopCause == WorkerRoundStopCause.Completed),
                group.Count(round => round.StopCause == WorkerRoundStopCause.Failed),
                group.Count(round => round.StopCause == WorkerRoundStopCause.Superseded),
                group.Count(round => round.StopCause is WorkerRoundStopCause.Cancelled or
                    WorkerRoundStopCause.Open or WorkerRoundStopCause.Unknown or WorkerRoundStopCause.Clarification),
                group.Sum(round => round.InputTokens ?? 0),
                group.Sum(round => round.CachedInputTokens ?? 0),
                group.Sum(round => round.OutputTokens ?? 0),
                group.Count(round => !round.UsageReported)))
            .ToArray();

    private static void WriteRows(TextWriter writer, string title,
        IReadOnlyList<OwnerDigestRoundRow> rows)
    {
        writer.WriteLine($"{title} | Rounds | Completed | Failed | Superseded | Other | Input | Cached input | Output | Usage unreported");
        foreach (var row in rows)
            writer.WriteLine(FormattableString.Invariant(
                $"{row.Key} | {row.Rounds} | {row.Completed} | {row.Failed} | {row.Superseded} | {row.Other} | {row.InputTokens} | {row.CachedInputTokens} | {row.OutputTokens} | {row.UsageUnreported}"));
    }

    private static string Part(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
