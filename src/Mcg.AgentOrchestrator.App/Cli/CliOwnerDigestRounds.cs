using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record OwnerDigestRoundRow(
    string Key, int Rounds, int Completed, int Failed, int Superseded, int Other,
    long InputTokens, long CachedInputTokens, long OutputTokens, int UsageUnreported);

internal sealed record OwnerDigestRoundsResult(
    IReadOnlyList<OwnerDigestRoundRow> ByRole,
    IReadOnlyList<OwnerDigestRoundRow> ByModel);

internal static class CliOwnerDigestRounds
{
    internal static OwnerDigestRoundsResult Aggregate(OwnerDigestResult digest, IEnumerable<Goal> goals)
    {
        var rounds = WorkerRoundLedger.FromGoals(goals)
            .Where(round => round.DispatchedAt >= digest.Since && round.DispatchedAt < digest.Until)
            .ToArray();
        return new OwnerDigestRoundsResult(
            Rows(rounds, round => round.Role.ToString()),
            Rows(rounds, round => $"{Part(round.ProviderName)}/{Part(round.ModelName)}"));
    }

    internal static void WriteText(TextWriter writer, OwnerDigestResult digest, IEnumerable<Goal> goals)
    {
        var result = Aggregate(digest, goals);
        WriteRows(writer, "Rounds by role", result.ByRole);
        WriteRows(writer, "Rounds by model", result.ByModel);
    }

    internal static void WriteJson(TextWriter writer, OwnerDigestResult digest, IEnumerable<Goal> goals)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var root = JsonSerializer.SerializeToNode(digest, options)!.AsObject();
        CliOwnerDigestCommand.AddEscapeSources(root, digest);
        root["rounds"] = JsonSerializer.SerializeToNode(Aggregate(digest, goals), options);
        writer.WriteLine(root.ToJsonString(options));
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
                    WorkerRoundStopCause.Open or WorkerRoundStopCause.Unknown),
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
