using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// A receipt describes one already reaped dispatch generation; replay never performs process I/O.
internal sealed record GoalWorkerTerminationReceipt(
    TaskId TaskId, TaskProcessRecord CancelledProcess,
    CancellationCandidateEvidence CandidateEvidence, IReadOnlyList<string> TaskNotes);

internal static class GoalWorkerTermination
{
    private static readonly AsyncLocal<Func<GoalSnapshot, TaskId, GoalWorkerTerminationReceipt>?> Override = new();

    internal static IDisposable Push(Func<GoalSnapshot, TaskId, GoalWorkerTerminationReceipt> terminate)
    {
        var previous = Override.Value;
        Override.Value = terminate;
        return new RestoreAction(() => Override.Value = previous);
    }

    internal static IReadOnlyList<GoalWorkerTerminationReceipt> Terminate(GoalSnapshot snapshot, string disposition)
    {
        var receipts = new List<GoalWorkerTerminationReceipt>();
        try
        {
            var selection = new AgentOrchestratorKernel();
            selection.ReplaceGoalWithSnapshot(snapshot);
            foreach (var task in selection.GetGoal(new GoalId(snapshot.Id)).Tasks
                .Where(task => task.LastProcess is { IsRunning: true }))
            {
                receipts.Add((Override.Value ?? TerminateDispatch)(snapshot, task.Id));
            }
            return receipts;
        }
        catch (Exception ex) when (receipts.Count > 0)
        {
            throw DidNotCommit(disposition, receipts, ex);
        }
    }

    private static GoalWorkerTerminationReceipt TerminateDispatch(GoalSnapshot snapshot, TaskId taskId)
    {
        // The existing runner owns reap, resource accounting, and planner sample delivery. Its
        // kernel writes are isolated here and become durable only when replayed in the transaction.
        var isolated = new AgentOrchestratorKernel();
        isolated.ReplaceGoalWithSnapshot(snapshot);
        var goalId = new GoalId(snapshot.Id);
        var goal = isolated.GetGoal(goalId);
        var task = isolated.GetTask(goalId, taskId);
        var original = task.LastProcess!;
        var timelineCount = goal.Timeline.Count;
        var cancelled = new BackgroundDispatchRunner().CancelLatestProcess(isolated, goalId, taskId);
        var candidate = CancellationCandidateEvidenceClassifier.Classify(
            task, original, goalId, new DispatchWorktreeCommitter());
        var notes = goal.Timeline.Skip(timelineCount)
            .Where(item => item.TaskId == taskId && item.Kind == ProgressKind.TaskNote &&
                !item.Message.StartsWith("CANCELLATION_CANDIDATE_EVIDENCE", StringComparison.Ordinal))
            .Select(item => item.Message).ToArray();
        return new(taskId, cancelled, candidate, notes);
    }

    internal static void Replay(AgentOrchestratorKernel kernel, GoalId goalId,
        IReadOnlyList<GoalWorkerTerminationReceipt> receipts)
    {
        foreach (var receipt in receipts)
        {
            var current = kernel.GetTask(goalId, receipt.TaskId).LastProcess;
            var cancelled = receipt.CancelledProcess;
            if (current is null || current.ProcessId != cancelled.ProcessId || current.StartedAt != cancelled.StartedAt ||
                current.Command != cancelled.Command || current.WorkingDirectory != cancelled.WorkingDirectory)
            {
                throw new InvalidOperationException($"Task {receipt.TaskId.Value[..8]} dispatch generation changed after termination.");
            }
            if (!current.IsRunning)
            {
                if (current.WasCancelled) continue;
                throw new InvalidOperationException($"Task {receipt.TaskId.Value[..8]} completed after termination preparation.");
            }
            kernel.RecordTaskProcessCancelled(goalId, receipt.TaskId, cancelled, receipt.CandidateEvidence);
            foreach (var note in receipt.TaskNotes) kernel.RecordTaskNote(goalId, receipt.TaskId, note);
        }
    }

    internal static InvalidOperationException DidNotCommit(string disposition,
        IReadOnlyList<GoalWorkerTerminationReceipt> receipts, Exception cause) => new(
        $"stop --as {disposition} did not commit after terminating workers: " +
        string.Join(", ", receipts.Select(receipt =>
            $"task {receipt.TaskId.Value[..8]} pid {receipt.CancelledProcess.ProcessId}")) + ".", cause);

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
