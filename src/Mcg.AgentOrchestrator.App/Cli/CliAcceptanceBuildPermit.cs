using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAcceptanceBuildPermit
{
    internal static (DotnetBuildEnvironmentLease Lease, int SlotIndex) Acquire(
        GoalId goalId,
        Func<TimeSpan?, Action<DotnetBuildStableSlotWait>?, DotnetBuildEnvironmentLease>? selector,
        TimeSpan? timeout,
        DotnetBuildStorageRoot? storageRoot,
        string repositoryRoot)
    {
        var onSlotWait = (DotnetBuildStableSlotWait wait) =>
            Console.WriteLine($"waiting for build-{wait.SlotIndex} permit held by pid {wait.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
        var stableSlotLease = selector is { } stableSlotSelector
            ? stableSlotSelector(timeout, onSlotWait)
            : SelectGoalBuildPermit(
                goalId,
                timeout,
                onSlotWait,
                storageRoot,
                repositoryRoot);
        if (storageRoot is { } configuredRoot &&
            (!configuredRoot.ContainsPath(stableSlotLease.Environment.RootPath) ||
             !configuredRoot.ContainsPath(stableSlotLease.Environment.ArtifactsPath) ||
             !configuredRoot.ContainsPath(stableSlotLease.Environment.ExecutionLockPath)))
        {
            stableSlotLease.Dispose();
            stableSlotLease = null;
            throw new InvalidOperationException("Acceptance build lease is outside the configured build storage namespace.");
        }
        var stableSlotIndex = stableSlotLease.Environment.BuildPermitIndex ??
            ParseStableSlotIndex(stableSlotLease.Environment.SlotOwnerToken);
        if (stableSlotIndex is null)
        {
            stableSlotLease.Dispose();
            throw new IOException($"Build permit did not identify its pool index: {stableSlotLease.Environment.SlotOwnerToken}");
        }

        return (stableSlotLease, stableSlotIndex.Value);
    }

    private static DotnetBuildEnvironmentLease SelectGoalBuildPermit(
        GoalId goalId,
        TimeSpan? timeout,
        Action<DotnetBuildStableSlotWait>? onWait,
        DotnetBuildStorageRoot? storageRoot,
        string repositoryRoot)
    {
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(
            goalId, "acceptance", storageRoot: storageRoot, repositoryRoot: repositoryRoot);
        if (environment.BuildPermitIndex is { } permitIndex &&
            !DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(permitIndex, storageRoot))
        {
            onWait?.Invoke(new DotnetBuildStableSlotWait(
                permitIndex,
                DotnetBuildEnvironmentManager.GetStableSlotExecutionLeaseOwner(permitIndex, storageRoot)));
        }

        return DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, timeout) switch
        {
            DotnetBuildLeaseAcquisition.Acquired acquired => acquired.Lease,
            DotnetBuildLeaseAcquisition.SlotsBusy busy => throw new DotnetBuildSlotsBusyException(busy),
            DotnetBuildLeaseAcquisition.BuildLockBlocked blocked => throw new BuildLockBlockedException(blocked.Attribution),
            _ => throw new InvalidOperationException("Unknown dotnet build lease acquisition result.")
        };
    }

    internal static string FormatBusySlots(IReadOnlyList<DotnetBuildStableSlotWait> busySlots) =>
        string.Join(
            "|",
            busySlots.Select(slot =>
                $"slot-{slot.SlotIndex}:pid-{slot.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));

    private static int? ParseStableSlotIndex(string slotOwnerToken)
    {
        return slotOwnerToken.StartsWith("build-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(slotOwnerToken[6..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var slotIndex)
                ? slotIndex
                : null;
    }
}
