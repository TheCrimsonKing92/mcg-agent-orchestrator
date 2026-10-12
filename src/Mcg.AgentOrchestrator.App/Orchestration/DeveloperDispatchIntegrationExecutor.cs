using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum DeveloperDispatchIntegrationRefusalKind
{
    None,
    OperationHeld,
    LeaseRefused,
    IntegrationRefused
}

internal sealed record DeveloperDispatchIntegrationExecution(
    DeveloperDispatchIntegrationRefusalKind RefusalKind,
    string? LeaseRecoveryStatus,
    DeveloperBranchIntegrationResult? Integration);

internal static class DeveloperDispatchIntegrationExecutor
{
    internal static DeveloperDispatchIntegrationExecution Execute(
        Goal goal,
        Func<Goal, GoalEvidenceOperationStart>? tryBeginOperation,
        Func<Goal, string, IDisposable?> tryAcquireLease,
        Func<Goal, DeveloperBranchIntegrationResult> integrate,
        Action<Goal, DeveloperBranchIntegrationResult> recordReceipt)
    {
        if (tryBeginOperation is not null)
        {
            var operationStart = tryBeginOperation(goal);
            if (operationStart.Scope is null)
            {
                var status = operationStart.LeaseFact is { } leaseFact
                    ? GoalEvidenceLeaseRecoveryStatuses.Format(leaseFact.RecoveryStatus)
                    : "state-unavailable";
                return new(DeveloperDispatchIntegrationRefusalKind.OperationHeld, status, null);
            }

            using (operationStart.Scope)
            {
                DeveloperBranchIntegrationResult integration;
                try
                {
                    integration = integrate(goal);
                    recordReceipt(goal, integration);
                }
                catch (OperationCanceledException ex)
                {
                    operationStart.Scope.Abort(ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    operationStart.Scope.Fail(ex.Message);
                    throw;
                }

                if (integration.CanDispatch)
                {
                    operationStart.Scope.Complete(integration.Message);
                    return new(DeveloperDispatchIntegrationRefusalKind.None, null, integration);
                }

                operationStart.Scope.Fail(integration.Message);
                return new(DeveloperDispatchIntegrationRefusalKind.IntegrationRefused, null, integration);
            }
        }

        using var integrationEvidenceMutationLease = tryAcquireLease(
            goal,
            "conductor:developer-branch-integration");
        if (integrationEvidenceMutationLease is null)
        {
            return new(DeveloperDispatchIntegrationRefusalKind.LeaseRefused, null, null);
        }

        var leaseIntegration = integrate(goal);
        recordReceipt(goal, leaseIntegration);
        return new(leaseIntegration.CanDispatch
            ? DeveloperDispatchIntegrationRefusalKind.None
            : DeveloperDispatchIntegrationRefusalKind.IntegrationRefused, null, leaseIntegration);
    }
}
