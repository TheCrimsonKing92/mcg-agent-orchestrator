namespace Mcg.AgentOrchestrator.Core;

public static class NextActionAutomationPolicy
{
    public static NextActionAutomationPlan Build(NextActionItem item)
    {
        return item.Kind switch
        {
            NextActionKind.RunAssignedTask when item.TaskId is not null =>
                Executable(NextActionAutomationKind.RunAssignedTask, item.TaskId, "Run the assigned model-backed task."),
            NextActionKind.RefreshRunningProcess when item.TaskId is not null =>
                Executable(NextActionAutomationKind.RefreshRunningProcess, item.TaskId, "Refresh the running background process."),
            NextActionKind.ExecuteRecordedDispatch when item.TaskId is not null =>
                Executable(NextActionAutomationKind.StartRecordedDispatch, item.TaskId, "Start the recorded dispatch in the background."),
            NextActionKind.DelegatePendingTask when item.TaskId is not null =>
                Executable(NextActionAutomationKind.DelegatePendingTask, item.TaskId, "Delegate pending work to configured agents."),
            NextActionKind.AnswerHumanInput =>
                Manual(item.TaskId, "Human input is required before the goal can advance."),
            NextActionKind.VerifyCompletedTask =>
                Manual(item.TaskId, "Verification requires a command or manual pass/fail note."),
            NextActionKind.FixFailedVerification =>
                Manual(item.TaskId, "Failed verification requires investigation before automated advance."),
            NextActionKind.InspectFailedTask =>
                Manual(item.TaskId, "Failed task inspection is required before automated advance."),
            NextActionKind.MonitorGoal =>
                Manual(item.TaskId, "Monitoring is read-only and does not advance work."),
            _ =>
                Manual(item.TaskId, "Next action is not executable without additional operator input.")
        };
    }

    private static NextActionAutomationPlan Executable(NextActionAutomationKind kind, TaskId taskId, string message)
    {
        return new NextActionAutomationPlan(true, kind, taskId, message);
    }

    private static NextActionAutomationPlan Manual(TaskId? taskId, string message)
    {
        return new NextActionAutomationPlan(false, NextActionAutomationKind.None, taskId, message);
    }
}
