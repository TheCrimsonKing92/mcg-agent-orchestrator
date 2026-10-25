using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FaultRecordedAcceptanceFailureRoute
{
    internal static FaultRecordedAcceptanceFailureDecision? Decide(Goal goal)
    {
        if (goal.LatestAcceptanceFailure is not { FailedChecks.Count: 1 } failure ||
            failure.CheckAttributions is { Count: > 0 } ||
            !failure.FailedChecks[0].StartsWith("background-acceptance-fault: ", StringComparison.Ordinal))
            return null;

        var check = failure.FailedChecks[0];
        var alreadyRegated = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.StartsWith("FAULT_RECORDED_REGATE ", StringComparison.Ordinal))
            .Select(evt => evt.Message.Split('\n')[0].TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(tokens => tokens.Contains("candidate_sha=" + failure.BranchHeadSha, StringComparer.Ordinal));
        var regate = !string.IsNullOrWhiteSpace(failure.BranchHeadSha) && !alreadyRegated &&
            WorkspaceProbeFaultClassifier.Classify(check) == WorkspaceProbeFaultKind.HostContentionTimeout;
        return new(regate, failure.BranchHeadSha,
            $"background acceptance fault recorded at AcceptanceFailed: {check}");
    }
}
