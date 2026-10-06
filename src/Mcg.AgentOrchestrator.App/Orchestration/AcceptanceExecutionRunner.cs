using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceExecutionRunner
{
    internal static AcceptanceVerificationResult RunAttempt(
        IGoalAcceptanceVerifier verifier,
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var executionOwner = AcceptanceExecutionOwners.CreateAttempt(
            worktreePath,
            goalId,
            stableSlotIndex,
            cancellationToken,
            executionOptions);
        return AcceptanceExecutionOwnerLifetime.Run(
            executionOwner,
            () => verifier.RunOwnedAsync(
                worktreePath,
                goalId,
                changedFiles,
                stableSlotIndex,
                stableSlotLease,
                executionOwner).GetAwaiter().GetResult());
    }

    internal static FocusedEvidenceRunResult RunFocusedVerification(
        IGoalAcceptanceVerifier verifier,
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        bool runBaselineArm,
        CancellationToken cancellationToken,
        FindingEvidenceNegativeControl? negativeControl = null, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null, IReadOnlyList<string>? declaredPaths = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var executionOwner = AcceptanceExecutionOwners.CreateFocusedVerification(
            worktreePath,
            goalId,
            stableSlotIndex,
            cancellationToken);
        return AcceptanceExecutionOwnerLifetime.Run(
            executionOwner,
            () => (negativeControl is { } mode
                ? verifier.RunNegativeControlFocusedEvidenceOwnedAsync(worktreePath, goalId, request,
                    executionOwner, mode, stableSlotIndex, stableSlotLease, runBaselineArm, revertPaths, mutation, declaredPaths)
                : verifier.RunFocusedEvidenceOwnedAsync(
                worktreePath,
                goalId,
                request,
                executionOwner,
                stableSlotIndex,
                stableSlotLease,
                runBaselineArm)).GetAwaiter().GetResult());
    }
}
