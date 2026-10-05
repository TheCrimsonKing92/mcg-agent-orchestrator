namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const string PassingVerificationClearedNote =
        "passing verification was cleared because the task was returned to Assigned.";

    private void ReconcileAssignedPassingVerification(Goal goal)
    {
        foreach (var task in goal.Tasks)
        {
            var cleared = task.ConsumePassingVerificationClearedOnAssignment();
            if (task.Status == WorkTaskStatus.Assigned && task.LastVerification?.Succeeded is true)
            {
                task.ClearLatestVerification();
                cleared = true;
            }

            if (cleared)
            {
                Append(goal, task.Id, ProgressKind.TaskNote, $"Task '{task.Id.Value}' {PassingVerificationClearedNote}");
            }
        }
    }
}
