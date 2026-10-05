namespace Mcg.AgentOrchestrator.Core;

public sealed partial class TaskSpec
{
    internal void SetDispatchFailedRoundCheckpointReceipt(FailedRoundCheckpointReceipt receipt)
    {
        if (LastDispatch is null)
            throw new InvalidOperationException("Cannot record failed-round edits without a dispatch.");
        ReplaceLastDispatch(LastDispatch with { FailedRoundCheckpointReceipt = receipt });
    }

    internal void RecordDispatchFailedRoundCheckpointDecision(
        DateTimeOffset dispatchedAt, FailedRoundCheckpointDecision decision)
    {
        var matches = _dispatchHistory.Select((dispatch, index) => (dispatch, index))
            .Where(item => item.dispatch.DispatchedAt == dispatchedAt).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Failed-round checkpoint requires one matching dispatch attempt.");
        var (dispatch, index) = matches[0];
        if (dispatch.FailedRoundCheckpointReceipt?.DispatchId != decision.DispatchId)
            throw new InvalidOperationException("Failed-round checkpoint decision must name its receipt's dispatch.");
        var updated = dispatch with
        {
            FailedRoundCheckpointDecision = decision,
            FailedRoundCheckpointReceipt = decision.Outcome == FailedRoundCheckpointOutcome.Committed
                ? null : dispatch.FailedRoundCheckpointReceipt
        };
        _dispatchHistory[index] = updated;
        if (LastDispatch?.DispatchedAt == dispatchedAt)
            LastDispatch = updated;
    }
}
