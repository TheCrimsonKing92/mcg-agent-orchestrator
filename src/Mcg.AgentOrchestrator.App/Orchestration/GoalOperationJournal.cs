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

internal enum GoalTerminalDispositionKind
{
    Landed,
    Retired
}

internal sealed record GoalTerminalDisposition(
    GoalTerminalDispositionKind Kind,
    string Detail);

internal sealed record GoalLifecycleJournalEntry(
    string IdempotencyKey,
    GoalId GoalId,
    string CommandName,
    string Objective,
    DateTimeOffset At);

internal static class GoalOperationJournal
{
    public const string TerminalDispositionOperation = "conductor:terminal-disposition";

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

    public static void RecordTerminalDisposition(
        string executionDirectory,
        Goal goal,
        GoalTerminalDisposition disposition)
    {
        Completed(executionDirectory, goal, TerminalDispositionOperation, JsonSerializer.Serialize(disposition, JsonOptions));
        Completed(executionDirectory, goal, "conductor:land", disposition.Detail);
        Completed(executionDirectory, goal, "conductor:record", disposition.Detail);
        Completed(executionDirectory, goal, "conductor:cleanup", disposition.Detail);
    }

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

    public static bool HasCompletedLandingEvidence(GoalOperationJournalSummary journal) =>
        HasRetiredTerminalDisposition(journal) ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)));

    public static bool HasCompletedRecordEvidence(GoalOperationJournalSummary journal) =>
        HasRetiredTerminalDisposition(journal) ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase)));

    public static bool HasCompletedCleanupEvidence(GoalOperationJournalSummary journal) =>
        HasRetiredTerminalDisposition(journal) ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase)));

    public static bool HasRetiredTerminalDisposition(GoalOperationJournalSummary journal)
    {
        var latestTerminalDisposition = journal.LatestByOperation
            .LastOrDefault(entry => entry.Operation.Equals(TerminalDispositionOperation, StringComparison.OrdinalIgnoreCase));
        if (latestTerminalDisposition is not null)
        {
            return IsRetiredTerminalDispositionEntry(latestTerminalDisposition);
        }

        return journal.LatestByOperation.Any(IsLegacyRetiredTerminalDispositionEntry);
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

    private static bool IsRetiredTerminalDispositionEntry(GoalOperationJournalEntry entry)
    {
        if (entry.Status != GoalOperationStatus.Completed)
        {
            return false;
        }

        if (entry.Operation.Equals(TerminalDispositionOperation, StringComparison.OrdinalIgnoreCase))
        {
            return TryDeserializeTerminalDisposition(entry.Detail) is { Kind: GoalTerminalDispositionKind.Retired };
        }

        return IsLegacyRetiredTerminalDispositionEntry(entry);
    }

    private static bool IsLegacyRetiredTerminalDispositionEntry(GoalOperationJournalEntry entry) =>
        entry.Status == GoalOperationStatus.Completed &&
        (entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase)) &&
        entry.Detail?.Contains("Terminal sweep retired missing goal artifact", StringComparison.OrdinalIgnoreCase) == true;

    private static GoalTerminalDisposition? TryDeserializeTerminalDisposition(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GoalTerminalDisposition>(detail, JsonOptions);
        }
        catch
        {
            return null;
        }
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
