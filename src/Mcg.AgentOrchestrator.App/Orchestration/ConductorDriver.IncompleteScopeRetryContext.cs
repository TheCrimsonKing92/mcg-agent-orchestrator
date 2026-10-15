using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private IReadOnlyList<string> BuildCriterionRetryFeedback(
        Goal goal, TaskId targetTaskId, FailedGoalRecoveryDecision decision)
    {
        var feedback = new List<string>
        {
            decision.FeedbackCommand ?? throw new InvalidOperationException("Criterion retry command evidence is missing."),
            decision.FeedbackEvidence ?? throw new InvalidOperationException("Criterion retry failure evidence is missing.")
        };
        if (decision.DiscriminatingEvidence != "real-worker-or-command-failure")
            return feedback;
        var task = goal.Tasks.Single(task => task.Id == targetTaskId);
        var verification = task.LastVerification;
        if (task.RequiredRole != AgentRole.Developer || verification is null ||
            !TaskOutcomeClassifier.IsIncompleteScopeDeclaration(TaskOutcomeClassifier.TryExtractRule(
                DispatchFailureClassifier.Classify(task, verification).ClassifierReceipt)))
            return feedback;

        feedback.Add(BuildPreviousRoundAccount(verification.AuthoritativeStandardOutput ?? verification.StandardOutput));
        try
        {
            var detail = BuildCandidateFailureDetailEntry(goal);
            if (detail is not null)
                feedback.Add(detail);
        }
        catch (Exception)
        {
            // Evidence enrichment must not prevent the already-decided retry.
        }
        return feedback;
    }

    internal static string BuildPreviousRoundAccount(string? output) => IncompleteScopeRetryEvidence.BuildPreviousRoundAccount(output);

    private string? BuildCandidateFailureDetailEntry(Goal goal)
    {
        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (string.IsNullOrEmpty(candidateSha))
            return null;

        var newest = IncompleteScopeRetryEvidence.SelectNewestCandidateRed(
            goal.Tasks.SelectMany(task => task.VerificationHistory), candidateSha);
        if (newest is null)
            return null;

        var rendered = AppendActionableCandidateRedFailureDetail(
            "Candidate failure detail:", newest.Value.ReceiptId,
            newest.Value.FailingTestIdentities, newest.Value.TestResultPaths);
        return rendered.Contains("[FAIL] ", StringComparison.Ordinal) ? rendered : null;
    }
}
