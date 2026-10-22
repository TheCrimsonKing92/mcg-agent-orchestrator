namespace Mcg.AgentOrchestrator.Core;

// Owns duplicate-question diagnostics and the bounded answered-request suppression policy.
internal static class HumanInputSuppression
{
    internal const int Threshold = 3;

    internal static void AppendContradictoryRecordAdvisoryIfNeeded(Goal goal, HumanInputRequest request,
        IEnumerable<HumanInputRequest> requests, Action<Goal, TaskId?, ProgressKind, string> append)
    {
        var matches = requests
            .Where(candidate =>
                candidate.GoalId == goal.Id &&
                candidate.HasSameIdentity(request))
            .OrderBy(candidate => candidate.RequestedAt)
            .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var distinctTaskIds = matches
            .Where(candidate => candidate.TaskId is not null)
            .Select(candidate => candidate.TaskId!)
            .Distinct()
            .ToArray();
        if (distinctTaskIds.Length < AgentOrchestratorKernel.DuplicateHumanInputDistinctTaskThreshold)
        {
            return;
        }

        var marker = $"questionFingerprint={request.QuestionFingerprint}";
        var priorSignal = goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.ContradictoryRecordDetected &&
                evt.Message.Contains(marker, StringComparison.Ordinal))
            .OrderByDescending(evt => evt.OccurredAt)
            .FirstOrDefault();
        if (priorSignal is not null && !matches.Any(candidate => candidate.AnsweredAt > priorSignal.OccurredAt))
        {
            return;
        }

        var roles = distinctTaskIds
            .Select(taskId => goal.FindTask(taskId).RequiredRole)
            .Distinct()
            .OrderBy(role => role)
            .ToArray();
        var totalOccurrences = matches.Sum(candidate => 1 + candidate.SuppressionCount);
        var answeredRequest = matches
            .Where(candidate => candidate.IsCompleted && !candidate.WasDismissed)
            .OrderByDescending(candidate => candidate.AnsweredAt)
            .FirstOrDefault();
        append(
            goal,
            null,
            ProgressKind.ContradictoryRecordDetected,
            $"kind=contradictory-record {marker} distinctTasks={distinctTaskIds.Length} " +
            $"totalOccurrences={totalOccurrences} tasks={string.Join(',', distinctTaskIds.Select(id => id.Value))} " +
            $"roles={string.Join(',', roles)} requests={string.Join(',', matches.Select(candidate => candidate.Id.Value))} " +
            $"alreadyAnsweredRequest={answeredRequest?.Id.Value ?? "none"}");
    }

    internal static void RestoreTaskAfterSuppressedAnsweredInput(
        Goal goal,
        TaskId? taskId,
        HumanInputRequest request,
        int? completedRound,
        string? workerResultLogReference, Action<Goal, TaskId?, ProgressKind, string> append)
    {
        if (taskId is not null)
        {
            var task = goal.FindTask(taskId);
            if (request.SuppressionCount >= Threshold)
            {
                if (task.Status != WorkTaskStatus.Completed)
                {
                    task.SetStatus(WorkTaskStatus.Failed);

                    if (request.SuppressionCount == Threshold)
                    {
                        append(
                            goal,
                            taskId,
                            ProgressKind.TaskFailed,
                            $"Answered human-input request {request.Id.Value[..8]} was genuinely re-raised by " +
                            $"completed worker round={completedRound?.ToString() ?? "unknown"}; " +
                            $"worker_result_log={FormatWorkerResultLogReference(workerResultLogReference)}; suppression threshold " +
                            $"{Threshold} reached. Automatic redispatch stopped.");
                    }

                }
            }
            else if (task.Status != WorkTaskStatus.Completed)
            {
                task.SetStatus(WorkTaskStatus.Assigned);
            }
        }

    }

    internal static void AppendDuplicateHumanInputSuppressed(
        Goal goal,
        TaskId? taskId,
        HumanInputRequest request,
        bool answered,
        Action<Goal, TaskId?, ProgressKind, string> append,
        int? completedRound = null,
        string? workerResultLogReference = null)
    {
        var thresholdReceipt = request.SuppressionCount == Threshold
            ? "; repeated suppression threshold reached"
            : string.Empty;
        append(
            goal,
            taskId,
            ProgressKind.DuplicateHumanInputSuppressed,
            $"kind=duplicate-human-input-suppressed request={request.Id.Value[..8]} " +
            $"count={request.SuppressionCount} state={(answered ? "answered" : "open")} " +
            $"round={completedRound?.ToString() ?? "unknown"} " +
            $"worker_result_log={FormatWorkerResultLogReference(workerResultLogReference)}{thresholdReceipt}");
    }

    private static string FormatWorkerResultLogReference(string? workerResultLogReference) =>
        string.IsNullOrWhiteSpace(workerResultLogReference)
            ? "unavailable"
            : workerResultLogReference.Trim();

}
