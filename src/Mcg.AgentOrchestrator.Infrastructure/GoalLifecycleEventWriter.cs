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

    public void AppendTimelineEvent(ProgressEvent progressEvent) =>
        Append(progressEvent.GoalId, ToLifecycleEventType(progressEvent.Kind), obj =>
        {
            obj["progressKind"] = progressEvent.Kind.ToString();
            obj["message"] = progressEvent.Message;
            obj["occurredAt"] = progressEvent.OccurredAt;
            if (progressEvent.TaskId is not null)
            {
                obj["taskId"] = progressEvent.TaskId.Value;
            }
        });

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

    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) =>
        Append(goalId, "GoalLanded", obj =>
        {
            obj["integrationBranch"] = integrationBranch;
            obj["goalBranch"] = goalBranch;
        });

    public void AppendCleanedUp(GoalId goalId) =>
        Append(goalId, "CleanedUp", _ => { });

    private void Append(GoalId goalId, string eventType, Action<JsonObject> addFields)
    {
        var key = goalId.Value;
        var fileLock = _locks.GetOrAdd(key, _ => new object());

        lock (fileLock)
        {
            var cursor = _nextCursors.GetOrAdd(key, _ => CountExistingLines(EventFilePath(goalId)));

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

    private static int CountExistingLines(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var count = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is not null)
        {
            count++;
        }

        return count;
    }

    private static string ToLifecycleEventType(ProgressKind kind) =>
        kind switch
        {
            ProgressKind.TaskDelegated => "TaskDelegated",
            ProgressKind.TaskDispatchRecorded => "TaskDispatched",
            ProgressKind.TaskCompleted => "TaskCompleted",
            ProgressKind.TaskVerificationRecorded => "TaskVerified",
            ProgressKind.HumanInputRequested => "GoalEscalated",
            ProgressKind.GoalPolicyDecision => "GoalLifecycleDecision",
            _ => kind.ToString()
        };
}
