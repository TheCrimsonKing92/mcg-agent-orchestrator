using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliOwnerDigestBoardFill
{
    internal static IReadOnlyList<string> Build(IReadOnlyList<BoardFillDraftRound> rounds,
        IReadOnlyList<Goal> goals, DateTimeOffset now) => rounds
        .Where(round => round.FinishedAt is { } finished && finished >= now.AddDays(-7) && finished <= now)
        .OrderBy(round => round.FinishedAt).ThenBy(round => round.Id, StringComparer.Ordinal)
        .Select(round => $"{Prefix(round.BacklogItemId)} | {round.Outcome} | fileable={((round.Assessment?.Fileable ?? false) ? "true" : "false")}" +
            $" | reasons={(round.Assessment is null ? "not-assessed" : Join(round.Assessment.FailingReasons, "; "))}" +
            $" | depends={Join(round.Assessment?.Scope.Depends.Select(dependency => Prefix(dependency.GoalId)) ?? [], ",")}" +
            $" | operator-goal={Join(goals.Where(goal => goal.SourceBacklogItemId == round.BacklogItemId).Select(goal => goal.Id.Value).Order(StringComparer.Ordinal), ",")}")
        .ToArray();

    internal static void WriteText(TextWriter writer, OrchestratorWorkspace workspace, IReadOnlyList<Goal> goals, DateTimeOffset now)
    {
        var path = ConductorBoardFillDraftStore.DefaultPath(workspace);
        if (!File.Exists(path)) return;
        try
        {
            var rows = Build(new ConductorBoardFillDraftStore(path).ReadAll(), goals, now);
            writer.WriteLine("Board-fill drafts (last 7 days):");
            foreach (var row in rows) writer.WriteLine(row);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or JsonException)
        { Console.Error.WriteLine($"Warning: board-fill drafts are unavailable: {exception.Message}"); }
    }

    private static string Prefix(string id) => id[..Math.Min(8, id.Length)];
    private static string Join(IEnumerable<string> values, string separator)
    {
        var text = string.Join(separator, values);
        return text.Length == 0 ? "none" : text;
    }
}
