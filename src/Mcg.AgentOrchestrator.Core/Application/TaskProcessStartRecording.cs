namespace Mcg.AgentOrchestrator.Core;

// Owns the task-local transition from a prepared dispatch to its matching running process.
internal static class TaskProcessStartRecording
{
    internal static void Record(TaskSpec task, TaskProcessRecord process)
    {
        if (task.LastDispatch is null)
        {
            throw new InvalidOperationException($"Task '{task.Id}' has no dispatch to start.");
        }

        if (!string.Equals(task.LastDispatch.Command, process.Command, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Process command does not match the recorded dispatch command.");
        }

        if (!string.Equals(task.LastDispatch.WorkingDirectory, process.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Process working directory does not match the recorded dispatch working directory.");
        }

        task.RecordProcess(process);
        var ownerlessAdmission = task.RetryAdmissionHistory.LastOrDefault(receipt =>
            receipt.LinkedDispatchAt == task.LastDispatch.DispatchedAt &&
            receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation &&
            string.IsNullOrWhiteSpace(receipt.ReservationOwnerId));
        if (ownerlessAdmission is not null)
            task.MarkRetryAdmissionStarted(task.LastDispatch.DispatchedAt, process.StartedAt);
        task.SetStatus(WorkTaskStatus.Running);
    }
}
