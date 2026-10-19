namespace Mcg.AgentOrchestrator.Core;

internal static class TerminalContractRepairStreak
{
    internal const string ResetNotePrefix = "TERMINAL_CONTRACT_REPAIR_STREAK_RESET ";
    internal const string InvalidationPrefix = "Invalidated ";

    internal static bool IsStreakRecord(TaskVerificationRecord record) =>
        record.CompletionVerdictRule == TaskOutcomeRules.VerificationTerminalContractIncomplete.Token ||
        DispatchFailureClassifier.IsProviderInterruptionFailure(record) ||
        (WorkerResultBlockers.TryGetTestsStatus(record, out var status) &&
         status == WorkerResultBlockers.TestsStatus.Inconclusive);

    internal static DateTimeOffset? LatestResetAt(IEnumerable<ProgressEvent> timeline, TaskId taskId) =>
        timeline.Where(evt => evt.TaskId == taskId &&
                ((evt.Kind == ProgressKind.TaskNote && evt.Message.StartsWith(ResetNotePrefix, StringComparison.Ordinal)) ||
                 (evt.Kind == ProgressKind.TaskRetried && evt.Message.StartsWith(InvalidationPrefix, StringComparison.Ordinal))))
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .Max();

    internal static int Count(IEnumerable<ProgressEvent> timeline, TaskSpec task)
    {
        var resetAt = LatestResetAt(timeline, task.Id);
        var count = 0;
        for (var index = task.VerificationHistory.Count - 1; index >= 0; index--)
        {
            var record = task.VerificationHistory[index];
            if (!IsStreakRecord(record) ||
                (count > 0 && resetAt is not null && record.CompletedAt <= resetAt.Value))
                break;
            count++;
        }
        return count;
    }

    internal static string? OperatorRetryResetNote(TaskSpec task, CriteriaCorrectionSource source)
    {
        if (task.RequiredRole != AgentRole.Tester ||
            source is not (CriteriaCorrectionSource.Operator or CriteriaCorrectionSource.AgentIntent) ||
            task.VerificationHistory.LastOrDefault() is not { } latest || !IsStreakRecord(latest))
            return null;

        var sourceToken = source == CriteriaCorrectionSource.Operator ? "operator" : "agent-intent";
        return $"{ResetNotePrefix}task={task.Id} reason=operator-retry source={sourceToken}";
    }
}
