using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Keeps review delivery identity separate from the source/config candidate identity.
internal static class FindingEvidenceDispatchContext
{
    internal static FindingEvidenceReviewContinuation? Continuation(TaskSpec task) =>
        task.PendingRetryCause == RetryCause.NewTestFinding ? task.FindingEvidenceReviewContinuation : null;

    internal static FindingEvidenceReviewContext? Capture(Goal goal, TaskSpec task, string? candidate,
        DateTimeOffset dispatchedAt, bool includePeerEvidence) =>
        task.RequiredRole == AgentRole.Reviewer && candidate is not null
            ? FindingEvidenceReviewContext.Capture(goal, task, candidate, dispatchedAt, includePeerEvidence) : null;

    internal static RetryContextFingerprint RetryFingerprint(Goal goal, TaskSpec task, string? providerName,
        string? modelName, PaidRouteClassification paidRoute, string? priorCandidate, string? currentCandidate, string? currentMainIdentity)
    {
        var fingerprint = RetryContextFingerprintFactory.Build(goal, task, providerName, modelName, paidRoute,
            priorCandidate, currentCandidate, currentMainIdentity);
        return Continuation(task) is { } continuation
            ? new RetryContextFingerprint(RetryContextFingerprint.CurrentSchemaVersion, continuation.Key)
            : fingerprint;
    }
}
