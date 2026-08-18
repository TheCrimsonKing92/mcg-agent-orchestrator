using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record GoalBoardAcceptanceFact(
    bool Available,
    bool IsLive,
    DateTimeOffset? LastHeartbeatAt)
{
    public static GoalBoardAcceptanceFact None { get; } = new(true, false, null);
    public static GoalBoardAcceptanceFact Unknown { get; } = new(false, false, null);
}

internal static class GoalBoardAcceptanceAttemptReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, GoalBoardAcceptanceFact> Read(
        string rootDirectory,
        IEnumerable<string> goalIds)
    {
        var results = new Dictionary<string, GoalBoardAcceptanceFact>(StringComparer.Ordinal);
        foreach (var goalId in goalIds.Distinct(StringComparer.Ordinal))
        {
            results[goalId] = ReadOne(rootDirectory, goalId);
        }

        return results;
    }

    private static GoalBoardAcceptanceFact ReadOne(string rootDirectory, string goalId)
    {
        var directory = Path.Combine(rootDirectory, goalId);
        if (!Directory.Exists(directory))
            return GoalBoardAcceptanceFact.None;

        try
        {
            var attempts = new List<ConductorParallelAcceptanceAttempt>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
            {
                var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    File.ReadAllText(path),
                    JsonOptions);
                if (attempt is null || !attempt.GoalId.Equals(goalId, StringComparison.Ordinal))
                    return GoalBoardAcceptanceFact.Unknown;
                attempts.Add(attempt);
            }

            var latest = attempts
                .OrderByDescending(attempt => attempt.StartedAt)
                .ThenByDescending(attempt => attempt.Ordinal)
                .ThenByDescending(attempt => attempt.AttemptId, StringComparer.Ordinal)
                .FirstOrDefault();
            return latest is null
                ? GoalBoardAcceptanceFact.None
                : new GoalBoardAcceptanceFact(
                    true,
                    latest.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running,
                    latest.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                        ? latest.LastHeartbeatAt
                        : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return GoalBoardAcceptanceFact.Unknown;
        }
    }
}
