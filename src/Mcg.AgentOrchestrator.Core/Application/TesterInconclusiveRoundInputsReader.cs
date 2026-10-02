using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Core;

public static class TesterInconclusiveRoundInputsReader
{
    // Receipt bodies can enrich older verification records in place, so completion timestamps
    // cannot reconstruct availability. Missing dispatch snapshots deliberately preserve retry.
    public static FailedGoalInconclusiveRoundPair? Read(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole != AgentRole.Tester || task.VerificationHistory.Count == 0)
            return null;
        var history = task.VerificationHistory;
        var current = ReadRound(task, history[^1]);
        if (current is null)
            return null;
        var previous = history.Count > 1 && history[^2].DispatchStartedAt != history[^1].DispatchStartedAt
            ? ReadRound(task, history[^2]) : null;
        return new FailedGoalInconclusiveRoundPair(previous, current);
    }

    public static FailedGoalInconclusiveRoundInputs? Capture(Goal goal, TaskSpec task, CandidateIdentity? candidate)
    {
        if (task.RequiredRole != AgentRole.Tester || candidate is null)
            return null;
        return new FailedGoalInconclusiveRoundInputs(candidate.Canonical, goal.Tasks
            .SelectMany(item => item.VerificationHistory)
            .SelectMany(record => record.FindingEvidenceReceipts ?? [])
            .Select(receipt => receipt.ReceiptId).ToImmutableArray());
    }

    public static FailedGoalInconclusiveRoundInputs? RecordedDispatchInputs(
        TaskSpec task, TaskVerificationRecord verification) =>
        task.RequiredRole == AgentRole.Tester && task.LastDispatch is { } dispatch &&
        verification.DispatchStartedAt is { } started && started >= dispatch.DispatchedAt &&
        string.Equals(dispatch.Command, verification.Command, StringComparison.Ordinal) &&
        string.Equals(dispatch.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            ? dispatch.InconclusiveRoundInputs : null;

    private static FailedGoalInconclusiveRoundInputs? ReadRound(TaskSpec task, TaskVerificationRecord record) =>
        DispatchFailureClassifier.Classify(task, record).Kind == DispatchOutcomeKind.VerificationInconclusive &&
        record.DispatchStartedAt is not null && record.CandidateIdentity is { } candidate &&
        record.InconclusiveRoundInputs is { } inputs &&
        string.Equals(candidate.Canonical, inputs.CandidateIdentity, StringComparison.Ordinal)
            ? inputs : null;
}
