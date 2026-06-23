using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalLifecycleEventWriter : IGoalLifecycleEventWriter
{
    private readonly string _eventsDirectory;
    private readonly IClock _clock;

    private readonly ConcurrentDictionary<string, object> _locks = new();
    private readonly ConcurrentDictionary<string, int> _nextCursors = new();

    public GoalLifecycleEventWriter(string eventsDirectory, IClock? clock = null)
    {
        _eventsDirectory = eventsDirectory;
        _clock = clock ?? new SystemClock();
    }

    public void AppendGoalCreated(GoalId goalId, string objective) =>
        Append(goalId, "GoalCreated", obj => { obj["objective"] = objective; });

    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) =>
        Append(goalId, "ClarificationNeeded", obj => { obj["clarificationId"] = clarificationId; });

    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) =>
        Append(goalId, "TaskDispatched", obj =>
        {
            obj["taskId"] = taskId.Value;
            obj["role"] = role.ToString();
            obj["workerName"] = workerName;
        });

    public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) =>
        Append(goalId, "WorkerProgress", obj =>
        {
            obj["stdoutBytes"] = stdoutBytes;
            obj["stderrBytes"] = stderrBytes;
            obj["lastProgressAt"] = lastProgressAt;
        });

    public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) =>
        Append(goalId, "AcceptanceResult", obj =>
        {
            obj["pass"] = pass;
            obj["failures"] = new JsonArray(failures.Select(f => JsonValue.Create(f)).ToArray<JsonNode?>());
        });

    public void AppendCleanedUp(GoalId goalId) =>
        Append(goalId, "CleanedUp", _ => { });

    private void Append(GoalId goalId, string eventType, Action<JsonObject> addFields)
    {
        var key = goalId.Value;
        var fileLock = _locks.GetOrAdd(key, _ => new object());

        lock (fileLock)
        {
            var cursor = _nextCursors.GetOrAdd(key, _ => 0);

            var obj = new JsonObject
            {
                ["cursor"] = cursor,
                ["timestamp"] = _clock.UtcNow,
                ["goalId"] = goalId.Value,
                ["eventType"] = eventType
            };
            addFields(obj);

            var line = obj.ToJsonString() + "\n";

            var path = EventFilePath(goalId);
            Directory.CreateDirectory(_eventsDirectory);
            File.AppendAllText(path, line);

            _nextCursors[key] = cursor + 1;
        }
    }

    public string EventFilePath(GoalId goalId) =>
        Path.Combine(_eventsDirectory, $"{goalId.Value}.jsonl");
}
