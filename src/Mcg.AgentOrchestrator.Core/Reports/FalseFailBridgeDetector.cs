namespace Mcg.AgentOrchestrator.Core;

/// <summary>Pairs dispatch evidence and detects file-change guard false failures.</summary>
internal static class FalseFailBridgeDetector
{
    internal static TaskVerificationRecord?[] PairVerifications(
        IReadOnlyList<TaskVerificationRecord> verifications,
        IReadOnlyList<(DateTimeOffset At, string? Command)> dispatches)
    {
        var paired = new TaskVerificationRecord?[dispatches.Count];
        var usedVerifications = new HashSet<TaskVerificationRecord>();
        for (var index = 0; index < dispatches.Count; index++)
        {
            var dispatch = dispatches[index];
            var nextDispatchAt = index + 1 < dispatches.Count ? dispatches[index + 1].At : (DateTimeOffset?)null;
            paired[index] = FindRoundVerification(verifications, usedVerifications, dispatch, nextDispatchAt);
            if (paired[index] is { } verification) usedVerifications.Add(verification);
        }
        return paired;
    }

    private static TaskVerificationRecord? FindRoundVerification(
        IReadOnlyList<TaskVerificationRecord> verifications,
        HashSet<TaskVerificationRecord> usedVerifications,
        (DateTimeOffset At, string? Command) dispatch,
        DateTimeOffset? nextDispatchAt)
    {
        var candidates = verifications.Where(verification =>
            !usedVerifications.Contains(verification) &&
            verification.CompletedAt >= dispatch.At &&
            (nextDispatchAt is null || verification.CompletedAt < nextDispatchAt.Value))
            .ToList();

        if (!string.IsNullOrWhiteSpace(dispatch.Command))
        {
            var commandMatch = candidates.FirstOrDefault(verification =>
                string.Equals(verification.Command, dispatch.Command, StringComparison.Ordinal));
            if (commandMatch is not null) return commandMatch;
        }
        return candidates.FirstOrDefault();
    }

    internal static bool HasLaterSuccess(TaskSpec task, IEnumerable<TaskVerificationRecord> verifications,
        DateTimeOffset after) => verifications.Any(candidate =>
            candidate.CompletedAt > after &&
            DispatchFailureClassifier.Classify(task, candidate).Kind == DispatchOutcomeKind.VerifiedSuccess);

    internal static bool IsFalseFailBridge(TaskVerificationRecord verification, bool laterSuccess,
        bool goalLanded) =>
        (verification.StandardError.Contains("did not produce required relevant file-change evidence", StringComparison.OrdinalIgnoreCase) ||
         verification.StandardError.Contains("did not produce relevant file-change evidence", StringComparison.OrdinalIgnoreCase)) &&
        (laterSuccess || goalLanded);
}
