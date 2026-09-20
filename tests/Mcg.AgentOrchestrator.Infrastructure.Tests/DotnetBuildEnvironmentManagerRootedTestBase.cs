using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public abstract class DotnetBuildEnvironmentManagerRootedTestBase : IDisposable
{
    protected DotnetBuildStorageRoot StorageRoot { get; } = new(CreateTempDirectory());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(StorageRoot.RootPath))
            {
                Directory.Delete(StorageRoot.RootPath, recursive: true);
            }
        }
        catch
        {
            // Best effort; failed facts may retain handles needed for failure inspection.
        }
    }
}

internal static class RootedDotnetBuildEnvironmentManager
{
    public static DotnetBuildEnvironment CreateAttempt(
        DotnetBuildStorageRoot storageRoot,
        GoalId? goalId,
        string attemptName,
        int slotCount = DotnetBuildEnvironmentManager.StableSlotCount) =>
        DotnetBuildEnvironmentManager.CreateAttempt(goalId, attemptName, slotCount, storageRoot);

    public static DotnetBuildEnvironment ResolveGoalEnvironment(DotnetBuildStorageRoot storageRoot, GoalId goalId) =>
        DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId, storageRoot);

    public static string GoalRoot(DotnetBuildStorageRoot storageRoot, GoalId goalId) =>
        DotnetBuildEnvironmentManager.GoalRoot(goalId, storageRoot);

    public static bool TryCleanupSuccessfulRun(DotnetBuildStorageRoot storageRoot, DotnetBuildEnvironment environment) =>
        DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(environment, storageRoot);

    public static DotnetBuildEnvironment CreateStableSlotAttempt(
        DotnetBuildStorageRoot storageRoot,
        int slotIndex,
        int slotCount = DotnetBuildEnvironmentManager.StableSlotCount) =>
        DotnetBuildEnvironmentManager.CreateStableSlotAttempt(slotIndex, slotCount, storageRoot);

    public static bool IsStableSlotExecutionLeaseAvailable(DotnetBuildStorageRoot storageRoot, int slotIndex) =>
        DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(slotIndex, storageRoot);

    public static DotnetBuildEnvironmentLease AcquireFirstAvailableStableSlotExecutionLock(
        DotnetBuildStorageRoot storageRoot,
        TimeSpan? timeout = null,
        Action<DotnetBuildStableSlotWait>? onWait = null,
        CancellationToken cancellationToken = default,
        int slotCount = DotnetBuildEnvironmentManager.StableSlotCount) =>
        DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            timeout, onWait, cancellationToken, slotCount, storageRoot);

    public static DotnetBuildLeaseAcquisition TryAcquireFirstAvailableStableSlotExecutionLock(
        DotnetBuildStorageRoot storageRoot,
        TimeSpan? timeout = null,
        Action<DotnetBuildStableSlotWait>? onWait = null,
        CancellationToken cancellationToken = default,
        int slotCount = DotnetBuildEnvironmentManager.StableSlotCount,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? sleep = null) =>
        DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
            timeout, onWait, cancellationToken, slotCount, timeProvider, sleep, storageRoot);

    public static bool TryRotateGoalLease(DotnetBuildStorageRoot storageRoot, GoalId goalId, string reason) =>
        DotnetBuildEnvironmentManager.TryRotateGoalLease(goalId, reason, storageRoot);

    public static bool TryDeleteGoalArtifacts(DotnetBuildStorageRoot storageRoot, GoalId goalId) =>
        DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId, storageRoot);

    public static DotnetBuildLeaseStatus InspectGoalLease(DotnetBuildStorageRoot storageRoot, GoalId goalId) =>
        DotnetBuildEnvironmentManager.InspectGoalLease(goalId, storageRoot);

    public static bool TryCleanupOrphanedGoalLease(
        DotnetBuildStorageRoot storageRoot,
        GoalId goalId,
        out DotnetBuildLeaseStatus status,
        out string detail) =>
        DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goalId, out status, out detail, storageRoot);
}
