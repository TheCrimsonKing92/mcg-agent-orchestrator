using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DispatchProviderSessionRetentionOptions(TimeSpan RetentionPeriod)
{
    public const string RetentionDaysEnvironmentVariable = "MCG_DISPATCH_PROVIDER_SESSION_RETENTION_DAYS";

    public static DispatchProviderSessionRetentionOptions Default { get; } = new(TimeSpan.FromDays(14));

    public static DispatchProviderSessionRetentionOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(RetentionDaysEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value) ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) ||
            double.IsNaN(days) ||
            double.IsInfinity(days))
        {
            return Default;
        }

        return new DispatchProviderSessionRetentionOptions(TimeSpan.FromDays(Math.Max(0, days)));
    }
}

public sealed class GoalLifecycleEventWriter : IGoalLifecycleEventWriter
{
    private readonly string _eventsDirectory;
    private readonly IClock _clock;
    private readonly AgentOrchestratorKernel? _kernel;
    private readonly DispatchProviderSessionRetentionOptions _sessionRetentionOptions;

    private readonly ConcurrentDictionary<string, object> _locks = new();
    private readonly ConcurrentDictionary<string, int> _nextCursors = new();
    private readonly AsyncLocal<string?> _activeDeliveryId = new();

    public GoalLifecycleEventWriter(
        string eventsDirectory,
        IClock? clock = null,
        AgentOrchestratorKernel? kernel = null,
        DispatchProviderSessionRetentionOptions? sessionRetentionOptions = null)
    {
        _eventsDirectory = eventsDirectory;
        _clock = clock ?? new SystemClock();
        _kernel = kernel;
        _sessionRetentionOptions = sessionRetentionOptions ?? DispatchProviderSessionRetentionOptions.FromEnvironment();
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

            if (progressEvent.RequeueSkipped is { } skipped)
            {
                obj["goalId"] = skipped.GoalId;
                obj["taskId"] = skipped.TaskId;
                obj["dispatchId"] = skipped.DispatchId;
                obj["blockingEntity"] = skipped.BlockingEntity;
                if (skipped.TerminalState is not null)
                {
                    obj["terminalState"] = skipped.TerminalState;
                }
                obj["reason"] = skipped.Reason;
                if (skipped.Detail is not null)
                {
                    obj["detail"] = skipped.Detail;
                }
            }
        });

    public void AppendGoalCreated(GoalId goalId, string objective) =>
        Append(goalId, "GoalCreated", obj => { obj["objective"] = objective; });

    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) =>
        Append(goalId, "ClarificationNeeded", obj => { obj["clarificationId"] = clarificationId; });

    public void AppendStaleClarificationDetected(
        GoalId goalId,
        IReadOnlyList<string> staleTopicKeys,
        string recoveryCommand) =>
        Append(goalId, "StaleClarificationDetected", obj =>
        {
            obj["staleTopicKeys"] = new JsonArray(staleTopicKeys.Select(key => JsonValue.Create(key)).ToArray<JsonNode?>());
            obj["recoveryCommand"] = recoveryCommand;
        });

    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) =>
        Append(goalId, "TaskDispatched", obj =>
        {
            obj["taskId"] = taskId.Value;
            obj["role"] = role.ToString();
            obj["workerName"] = workerName;
        });

    public void AppendAcceptanceCriterionWaived(
        GoalId goalId,
        string criterion,
        string actor,
        DateTimeOffset recordedAt,
        string reason,
        string capturedAcceptanceCriteriaHash) =>
        Append(goalId, "AcceptanceCriterionWaived", obj =>
        {
            obj["criterion"] = criterion;
            obj["actor"] = actor;
            obj["recordedAt"] = recordedAt;
            obj["reason"] = reason;
            obj["capturedAcceptanceCriteriaHash"] = capturedAcceptanceCriteriaHash;
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

    public void AppendGoalLandedFromAncestry(
        GoalId goalId,
        string goalBranch,
        string branchTip,
        string mainSha) =>
        Append(goalId, "GoalLanded", obj =>
        {
            obj["integrationBranch"] = "main";
            obj["goalBranch"] = goalBranch;
            obj["branchTip"] = branchTip;
            obj["mainSha"] = mainSha;
            obj["source"] = "ancestry";
        });

    public void AppendGoalLandedFromMergeEvidence(
        GoalId goalId,
        string goalBranch,
        string integrateSha,
        string mainSha) =>
        Append(goalId, "GoalLanded", obj =>
        {
            obj["integrationBranch"] = "main";
            obj["goalBranch"] = goalBranch;
            obj["integrateSha"] = integrateSha;
            obj["mainSha"] = mainSha;
            obj["source"] = "merge-evidence";
        });

    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) =>
        AppendGoalEscalated(
            goalId,
            state,
            _kernel?.Goals.FirstOrDefault(goal => goal.Id == goalId)?.Status ?? GoalStatus.Active,
            reason,
            source);

    public void AppendGoalEscalated(
        GoalId goalId,
        GoalLifecycleState state,
        GoalStatus status,
        string reason,
        string source) =>
        Append(goalId, "GoalEscalated", obj =>
        {
            obj["state"] = state.ToString();
            obj["status"] = status.ToString();
            obj["reason"] = reason;
            obj["source"] = source;
        });

    public void AppendGoalEvictedFromConductor(GoalId goalId, GoalStatus status, string trigger) =>
        Append(goalId, "GoalEvictedFromConductor", obj =>
        {
            obj["status"] = status.ToString();
            obj["trigger"] = trigger;
        });

    public void AppendCleanedUp(GoalId goalId)
    {
        var cleanedUpAt = _clock.UtcNow;
        Append(goalId, "CleanedUp", obj =>
        {
            obj["providerSessionRetentionDays"] = _sessionRetentionOptions.RetentionPeriod.TotalDays;
            obj["providerSessionRetireAfter"] = cleanedUpAt.Add(_sessionRetentionOptions.RetentionPeriod);
        });
        RetireDispatchProviderSessions(_kernel, goalId, cleanedUpAt, _sessionRetentionOptions);
    }

    public static int RetireDispatchProviderSessions(
        AgentOrchestratorKernel? kernel,
        GoalId goalId,
        DateTimeOffset cleanedUpAt,
        DispatchProviderSessionRetentionOptions? sessionRetentionOptions = null)
    {
        if (kernel is null)
        {
            return 0;
        }

        var options = sessionRetentionOptions ?? DispatchProviderSessionRetentionOptions.FromEnvironment();
        var retireAt = cleanedUpAt.Add(options.RetentionPeriod);
        Goal goal;
        try
        {
            goal = kernel.GetGoal(goalId);
        }
        catch (KeyNotFoundException)
        {
            return 0;
        }

        var retired = 0;
        foreach (var task in goal.Tasks)
        {
            var dispatch = task.LastDispatch;
            if (dispatch is null ||
                string.IsNullOrWhiteSpace(dispatch.ProviderSessionId) ||
                dispatch.ProviderSessionRetiredAt is not null)
            {
                continue;
            }

            kernel.RetireDispatchProviderSession(goalId, task.Id, retireAt);
            retired++;
        }

        return retired;
    }

    public void AppendProgressiveReviewGlanceReceipt(
        GoalId goalId,
        TaskId taskId,
        string trigger,
        string inputsHash,
        string verdict,
        string note,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        TimeSpan wallTime,
        string? model,
        string? profile) =>
        Append(goalId, "ProgressiveReviewGlanceReceipt", obj =>
        {
            obj["taskId"] = taskId.Value;
            obj["trigger"] = trigger;
            obj["inputsHash"] = inputsHash;
            obj["verdict"] = verdict;
            obj["note"] = note;
            obj["inputTokens"] = inputTokens;
            obj["outputTokens"] = outputTokens;
            obj["totalTokens"] = totalTokens;
            obj["wallTimeMs"] = (long)wallTime.TotalMilliseconds;
            obj["model"] = model;
            obj["profile"] = profile;
        });

    public void AppendProgressiveReviewGlanceGuardReceipt(
        GoalId goalId,
        TaskId taskId,
        ProgressiveReviewGlanceGuardReceipt receipt) =>
        Append(goalId, "ProgressiveReviewGlanceGuardReceipt", obj =>
        {
            obj["taskId"] = taskId.Value;
            obj["scopeConfidence"] = receipt.ScopeConfidence;
            obj["trustedScopePaths"] = new JsonArray(receipt.TrustedScopePaths.Select(path => JsonValue.Create(path)).ToArray());
            obj["changedFiles"] = new JsonArray(receipt.ChangedFiles.Select(path => JsonValue.Create(path)).ToArray());
            obj["note"] = receipt.Note;
            obj["evidenceLine"] = receipt.EvidenceLine;
            obj["reasonCode"] = receipt.ReasonCode;
            obj["legacyPhraseHintMatched"] = receipt.LegacyPhraseHintMatched;
            obj["originalVerdict"] = receipt.OriginalVerdict;
            obj["finalVerdict"] = receipt.FinalVerdict;
            obj["downgraded"] = receipt.Downgraded;
            obj["downgradeReason"] = receipt.DowngradeReason;
            obj["structuralComparison"] = receipt.StructuralComparison;
            obj["gateAnnotations"] = new JsonArray((receipt.GateAnnotations ?? [])
                .Select(annotation => JsonValue.Create(annotation)).ToArray());
            obj["operatorContextTruncated"] = receipt.OperatorContextTruncated;
            obj["cancellationWithheld"] = receipt.CancellationWithheld;
        });

    public void AppendProgressiveReviewGlanceCircuitReceipt(
        GoalId goalId,
        TaskId taskId,
        ProgressiveReviewGlanceCircuitReceipt receipt) =>
        Append(goalId, "ProgressiveReviewGlanceCircuitReceipt", obj =>
        {
            obj["taskId"] = taskId.Value;
            obj["circuitIdentity"] = receipt.CircuitIdentity;
            obj["admissionOutcome"] = receipt.AdmissionOutcome;
            obj["openingCause"] = receipt.OpeningCause;
            obj["originalReason"] = receipt.OriginalReason;
            obj["actualTokens"] = receipt.ActualTokens;
            obj["avoidedCallCount"] = receipt.AvoidedCallCount;
            obj["avoidedInputTokens"] = receipt.AvoidedInputTokens;
            obj["avoidedOutputTokens"] = receipt.AvoidedOutputTokens;
            obj["outputEstimateUnavailableReason"] = receipt.OutputEstimateUnavailableReason;
            obj["admissionLatencyMs"] = receipt.AdmissionLatencyMilliseconds;
            obj["changedFilesTriggerCount"] = receipt.ChangedFilesTriggerCount;
            obj["elapsedTriggerCount"] = receipt.ElapsedTriggerCount;
            obj["probeOutcome"] = receipt.ProbeOutcome;
        });

    public void AppendProgressiveReviewGlanceSummary(
        GoalId goalId,
        int totalGlances,
        int onTrack,
        int concern,
        int fundamentalMisdirection,
        int invalid,
        int totalTokens) =>
        Append(goalId, "ProgressiveReviewGlanceSummary", obj =>
        {
            obj["totalGlances"] = totalGlances;
            obj["onTrack"] = onTrack;
            obj["concern"] = concern;
            obj["fundamentalMisdirection"] = fundamentalMisdirection;
            obj["invalid"] = invalid;
            obj["totalTokens"] = totalTokens;
        });

    /// <summary>
    /// Appends one externally-journaled effect exactly once. The state outbox serializes competing
    /// processors; the event-file lock makes the lookup and append atomic within that processor.
    /// </summary>
    public void AppendIdempotent(
        GoalId goalId,
        string deliveryId,
        Action<IGoalLifecycleEventWriter> append)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        ArgumentNullException.ThrowIfNull(append);
        var fileLock = _locks.GetOrAdd(goalId.Value, _ => new object());
        lock (fileLock)
        {
            if (ContainsDeliveryId(EventFilePath(goalId), deliveryId))
                return;

            var priorDeliveryId = _activeDeliveryId.Value;
            _activeDeliveryId.Value = deliveryId;
            try
            {
                append(this);
            }
            finally
            {
                _activeDeliveryId.Value = priorDeliveryId;
            }
        }
    }

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
            if (_activeDeliveryId.Value is { } deliveryId)
                obj["deliveryId"] = deliveryId;

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

    private static bool ContainsDeliveryId(string path, string deliveryId)
    {
        if (!File.Exists(path))
            return false;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("deliveryId", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    string.Equals(value.GetString(), deliveryId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Legacy or torn lines are counted by the existing reader but cannot prove delivery.
            }
        }

        return false;
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
