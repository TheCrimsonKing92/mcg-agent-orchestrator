using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalAcceptanceVerifierOwnedTestExtensions
{
    internal static async Task<AcceptanceVerificationResult> RunOwnedAsync(
        this GoalAcceptanceVerifier verifier,
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions executionOptions)
    {
        var owner = AcceptanceExecutionOwners.CreateAttempt(
            worktreePath,
            goalId,
            stableSlotIndex,
            cancellationToken,
            executionOptions);
        await using (owner.ConfigureAwait(false))
        {
            return await verifier.RunOwnedAsync(
                worktreePath,
                goalId,
                changedFiles,
                stableSlotIndex,
                stableSlotLease,
                owner).ConfigureAwait(false);
        }
    }
}
