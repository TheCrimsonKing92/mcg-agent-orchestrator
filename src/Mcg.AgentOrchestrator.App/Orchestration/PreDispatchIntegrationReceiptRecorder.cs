using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PreDispatchIntegrationReceiptRecorder(AgentOrchestratorKernel kernel)
{
    internal void Record(Goal goal, DeveloperBranchIntegrationResult integration)
    {
        var eligibleTasks = goal.Tasks
            .Where(task =>
                task.RequiredRole == AgentRole.Developer &&
                task.Status == WorkTaskStatus.Assigned &&
                task.PendingRetryCause == RetryCause.MainDriftConflict &&
                task.LatestRetryAt is not null)
            .ToArray();
        if (eligibleTasks.Length == 0 || integration.Status != DeveloperBranchIntegrationStatus.Integrated)
        {
            return;
        }

        if (!PreDispatchIntegrationReceipt.IsCommitSha(integration.OriginalCandidateSha) ||
            !PreDispatchIntegrationReceipt.IsCommitSha(integration.IntegratedMainSha) ||
            !PreDispatchIntegrationReceipt.IsCommitSha(integration.ResultingCandidateSha))
        {
            throw new InvalidOperationException(
                "An integrated Developer retry must expose its original candidate, integrated main, and resulting candidate identities.");
        }

        foreach (var task in eligibleTasks)
        {
            kernel.RecordPreDispatchIntegrationReceipt(
                goal.Id,
                task.Id,
                new PreDispatchIntegrationReceipt(
                    goal.Id,
                    task.Id,
                    task.LatestRetryAt!.Value,
                    integration.OriginalCandidateSha!,
                    integration.IntegratedMainSha!,
                    integration.ResultingCandidateSha!));
        }
    }
}
