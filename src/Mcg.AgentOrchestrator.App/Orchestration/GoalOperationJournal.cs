using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalOperationStatus
{
    Begin,
    Completed,
    Failed,
    Skipped
}

internal sealed record GoalOperationJournalEntry(
    string IdempotencyKey,
    GoalId GoalId,
    string Operation,
    GoalOperationStatus Status,
    DateTimeOffset At,
    string? Detail);

internal sealed record GoalOperationJournalSummary(
    string Path,
    IReadOnlyList<GoalOperationJournalEntry> Entries,
    IReadOnlyList<GoalOperationJournalEntry> LatestByOperation,
    IReadOnlyList<GoalOperationJournalEntry> InterruptedOperations)
{
    public bool HasEntries => Entries.Count > 0;
}

internal sealed record GoalLifecycleJournalEntry(
    string IdempotencyKey,
    GoalId GoalId,
    string CommandName,
    string Objective,
    DateTimeOffset At);

internal static class GoalOperationJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static GoalOperationJournal()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static string PathFor(string executionDirectory, GoalId goalId) =>
        System.IO.Path.Combine(
            System.IO.Path.GetFullPath(executionDirectory),
            ".orchestrator",
            "goal-operations",
            $"{goalId.Value}.jsonl");

    public static string LifecycleIndexPath(string executionDirectory) =>
        System.IO.Path.Combine(
            System.IO.Path.GetFullPath(executionDirectory),
            ".orchestrator",
            "goal-operations",
            "lifecycle-index.jsonl");

    public static string Key(GoalId goalId, string operation) =>
        $"{goalId.Value}:{operation}".ToLowerInvariant();

    public static string LifecycleKey(string commandName, string objective)
    {
        var normalized = $"{commandName.Trim().ToLowerInvariant()}\n{objective.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static GoalId? TryFindLifecycleGoal(string executionDirectory, string commandName, string objective)
    {
        var path = LifecycleIndexPath(executionDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        var key = LifecycleKey(commandName, objective);
        return File.ReadLines(path)
            .Select(TryDeserializeLifecycle)
            .Where(entry => entry is not null && entry.IdempotencyKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry!.GoalId)
            .LastOrDefault();
    }

    public static void RecordLifecycleGoal(string executionDirectory, Goal goal, string commandName, string objective)
    {
        var path = LifecycleIndexPath(executionDirectory);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new GoalLifecycleJournalEntry(
            LifecycleKey(commandName, objective),
            goal.Id,
            commandName,
            objective,
            DateTimeOffset.UtcNow);
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
    }

    public static void Begin(string executionDirectory, Goal goal, string operation, string? detail = null) =>
        Append(executionDirectory, goal.Id, operation, GoalOperationStatus.Begin, detail);

    public static void Completed(string executionDirectory, Goal goal, string operation, string? detail = null) =>
        Append(executionDirectory, goal.Id, operation, GoalOperationStatus.Completed, detail);

    public static void Failed(string executionDirectory, Goal goal, string operation, string? detail = null) =>
        Append(executionDirectory, goal.Id, operation, GoalOperationStatus.Failed, detail);

    public static GoalOperationJournalSummary Read(string executionDirectory, GoalId goalId)
    {
        var path = PathFor(executionDirectory, goalId);
        if (!File.Exists(path))
        {
            return new GoalOperationJournalSummary(path, [], [], []);
        }

        var entries = ReadEntries(path);
        return BuildSummary(path, entries);
    }

    public static IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> ReadAll(string executionDirectory) =>
        ReadAll(executionDirectory, []);

    public static IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> ReadAll(
        string executionDirectory,
        IEnumerable<GoalId> expectedGoalIds)
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(executionDirectory),
            ".orchestrator",
            "goal-operations");
        var summaries = new Dictionary<GoalId, GoalOperationJournalSummary>();
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (fileName.Equals("lifecycle-index", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var goalId = new GoalId(fileName);
                summaries[goalId] = BuildSummary(path, ReadEntries(path));
            }
        }

        foreach (var goalId in expectedGoalIds)
        {
            summaries.TryAdd(goalId, new GoalOperationJournalSummary(PathFor(executionDirectory, goalId), [], [], []));
        }

        return summaries;
    }

    private static GoalOperationJournalEntry[] ReadEntries(string path) =>
        File.ReadLines(path)
            .Select(TryDeserialize)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderBy(entry => entry.At)
            .ToArray();

    private static GoalOperationJournalSummary BuildSummary(string path, GoalOperationJournalEntry[] entries)
    {
        var latest = entries
            .GroupBy(entry => entry.IdempotencyKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(entry => entry.At).Last())
            .OrderBy(entry => entry.At)
            .ToArray();
        var interrupted = latest
            .Where(entry => entry.Status == GoalOperationStatus.Begin)
            .ToArray();
        return new GoalOperationJournalSummary(path, entries, latest, interrupted);
    }

    private static void Append(
        string executionDirectory,
        GoalId goalId,
        string operation,
        GoalOperationStatus status,
        string? detail)
    {
        var path = PathFor(executionDirectory, goalId);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new GoalOperationJournalEntry(
            Key(goalId, operation),
            goalId,
            operation,
            status,
            DateTimeOffset.UtcNow,
            detail);
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        TryAppendRunEvent(executionDirectory, entry);
    }

    private static void TryAppendRunEvent(string executionDirectory, GoalOperationJournalEntry entry)
    {
        try
        {
            var storePath = System.IO.Path.Combine(
                System.IO.Path.GetFullPath(executionDirectory),
                ".orchestrator",
                "run-events.db");
            var store = new SqliteRunEventStore(storePath);
            store.AppendAsync(new RunEventAppend(
                RunEventTypes.GoalOperation,
                entry.GoalId.Value,
                entry.Operation,
                entry.Status.ToString(),
                entry.Detail,
                JsonSerializer.Serialize(entry, JsonOptions),
                entry.At))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // The journal is still the command's primary lifecycle side effect; observability must not
            // make dispatch or acceptance fail.
        }
    }

    private static GoalOperationJournalEntry? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<GoalOperationJournalEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GoalLifecycleJournalEntry? TryDeserializeLifecycle(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<GoalLifecycleJournalEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
