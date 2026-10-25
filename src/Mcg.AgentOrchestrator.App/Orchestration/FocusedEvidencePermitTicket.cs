using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FocusedEvidencePermitTicket
{
    internal static string Key(GoalId goalId) => "focused-evidence:goal-" + goalId.Value;

    internal static void Adopt(DotnetBuildStorageRoot storageRoot, TimeProvider timeProvider,
        GoalId goalId, int ownerPid)
    {
        var key = Key(goalId);
        try
        {
            new BuildPermitWaitQueue(storageRoot, timeProvider).Adopt(
                new BuildPermitClaimant(key, BuildPermitClaimantKind.FocusedEvidence, ownerPid, OwnerBound: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"BUILD_PERMIT_QUEUE_UNAVAILABLE claimant={key} reason={ex.GetType().Name}");
        }
    }

    internal static void Remove(DotnetBuildStorageRoot storageRoot, TimeProvider timeProvider, GoalId goalId)
    {
        var key = Key(goalId);
        try
        {
            new BuildPermitWaitQueue(storageRoot, timeProvider).Remove(
                new BuildPermitClaimant(key, BuildPermitClaimantKind.FocusedEvidence, Environment.ProcessId, OwnerBound: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"BUILD_PERMIT_QUEUE_UNAVAILABLE claimant={key} reason={ex.GetType().Name}");
        }
    }
}
