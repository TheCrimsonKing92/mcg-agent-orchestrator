using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerLandingsQuery
{
    internal const string Usage = "usage: landings [today|30]";
    private const string Unavailable = "acceptance evidence unavailable";

    internal sealed record Row(string GoalId, string Title, DateTimeOffset LocalLandingTime,
        string Revision, string Kind, string Acceptance, string? Epic = null);
    internal sealed record Result(bool InvalidWindow, string WindowDescription,
        IReadOnlyList<Row> Rows, int UndatedCount);

    internal static Result Load(IOrchestratorStateQueries state, string orchestratorDirectory,
        TimeProvider clock, string windowArgument, IOwnerGoalEpicLookup? epics = null)
    {
        var days = windowArgument.Trim() switch { "" => 7, "today" => 1, "30" => 30, _ => 0 };
        if (days == 0) return new(true, "", [], 0);
        epics ??= new PortfolioGoalEpicLookup(Path.Combine(orchestratorDirectory, "portfolio.db"));
        var now = clock.GetUtcNow();
        var zone = clock.LocalTimeZone;
        var firstDate = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1 - days);
        var goals = state.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var rows = new List<Row>();
        var undated = 0;
        foreach (var goal in goals)
        {
            // Non-null facts have already passed GoalPromotionFact's UTC/revision invariants.
            if (goal.PromotionFact is not { } fact)
            {
                if (goal.Status == nameof(GoalStatus.Completed)) undated++;
                continue;
            }
            var local = TimeZoneInfo.ConvertTime(fact.PromotedAt, zone);
            // Compare calendar dates rather than converting a possibly invalid DST midnight.
            if (local.Date < firstDate || fact.PromotedAt > now) continue;
            var kind = fact.Provenance switch
            {
                GoalPromotionFact.SoloProvenance => "solo",
                GoalPromotionFact.CohortProvenance => "cohort",
                GoalPromotionFact.MergeTrainProvenance => "merge train",
                _ => "unknown"
            };
            rows.Add(new(goal.Id, OwnerGoalTitle.From(goal.Objective), local,
                Prefix(fact.PromotedMainRevision, 9), kind,
                ReadAcceptance(orchestratorDirectory, goal.Id, fact.CandidateRevision, zone),
                ReadEpic(epics, goal.Id)));
        }
        return new(false, days == 1 ? "today" : $"the last {days} local calendar days",
            rows.OrderByDescending(row => row.LocalLandingTime)
                .ThenBy(row => row.GoalId, StringComparer.Ordinal).ToArray(), undated);
    }

    internal static IReadOnlyList<string> Render(Result result)
    {
        if (result.InvalidWindow) return [Usage];
        var lines = new List<string>();
        foreach (var row in result.Rows)
        {
            var epic = row.Epic is null ? "" : $"  current epic: {row.Epic}";
            lines.Add($"{Prefix(row.GoalId, 8)}  {Time(row.LocalLandingTime)}  {row.Revision}  {row.Kind}{epic}  current title: {row.Title}");
            lines.Add("  " + row.Acceptance);
        }
        if (result.Rows.Count == 0) lines.Add($"No landings recorded in {result.WindowDescription}.");
        lines.Add($"Undated: {result.UndatedCount} landed goals have no recorded landing time");
        return lines;
    }

    internal static string Text(IOrchestratorStateQueries state, string orchestratorDirectory,
        TimeProvider clock, string windowArgument, IOwnerGoalEpicLookup? epics = null)
    {
        try { return string.Join("\n", Render(Load(state, orchestratorDirectory, clock, windowArgument, epics))); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return "landings unavailable: " + ex.Message; }
    }

    private static string? ReadEpic(IOwnerGoalEpicLookup epics, string goalId)
    {
        try
        {
            var title = epics.GetTitleAsync(goalId, CancellationToken.None).GetAwaiter().GetResult()
                ?.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static string ReadAcceptance(string directory, string goalId, string candidate, TimeZoneInfo zone)
    {
        if (goalId is "" or "." or ".." || goalId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Unavailable;
        DateTimeOffset? newest = null;
        string? attemptId = null;
        try
        {
            var folder = Path.Combine(directory, "acceptance-gate-attempts", goalId);
            foreach (var path in Directory.EnumerateFiles(folder, "*.attempt.json"))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    var root = document.RootElement;
                    if (String(root, "kind") != "gate" ||
                        String(root, "detail")?.StartsWith("acceptance passed", StringComparison.Ordinal) != true ||
                        !string.Equals(String(root, "branchHeadSha"), candidate, StringComparison.OrdinalIgnoreCase) ||
                        !root.TryGetProperty("completedAt", out var completed) ||
                        completed.ValueKind != JsonValueKind.String || !completed.TryGetDateTimeOffset(out var at)) continue;
                    if (newest is not null && at <= newest.Value) continue;
                    var id = String(root, "attemptId");
                    attemptId = string.IsNullOrWhiteSpace(id) ? Path.GetFileName(path)[..^".attempt.json".Length] : id;
                    newest = at;
                }
                catch (Exception ex) when (ReadFailure(ex)) { }
            }
        }
        catch (Exception ex) when (ReadFailure(ex)) { return Unavailable; }
        return newest is { } time && !string.IsNullOrEmpty(attemptId)
            ? $"acceptance {Prefix(attemptId, 8)} passed {Time(TimeZoneInfo.ConvertTime(time, zone))}"
            : Unavailable;
    }

    private static string? String(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException;
    private static string Prefix(string value, int length) => value[..Math.Min(value.Length, length)];
    private static string Time(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " local";
}
