/// <summary>
/// Base for fixtures whose cost is host capacity: real git subprocesses, real worktrees,
/// SQLite state files and build-permit directories. Each test holds one slot of
/// <see cref="HostCapacityTestBudget"/> for its whole body, so these families overlap at a
/// bounded degree of parallelism instead of an unbounded one.
///
/// This replaces the removed all-or-nothing GoalWorktreeCleanupHooks serialization. It is
/// deliberately weaker: it admits several tests at once and imposes no ordering, so it
/// cannot stand in for a nonparallel collection guarding process-global state.
/// </summary>
public abstract class HostCapacityBoundTestBase : Xunit.IAsyncLifetime
{
    private readonly object capacityGate = new();
    private Task<IAsyncDisposable>? capacityAcquisition;
    private Task? capacityDisposal;
    private bool disposed;

    /// <summary>
    /// The budget this fixture draws from. Overridden only by the controls that verify the
    /// lifetime contract against an isolated budget instead of the shared process one.
    /// </summary>
    private protected virtual HostCapacityTestBudget CapacityBudget => HostCapacityTestBudget.Shared;

    public virtual ValueTask InitializeAsync() => EnsureHostCapacityAsync();

    /// <summary>One acquisition point for both eager fixtures and resource-using CLI helpers.</summary>
    private protected async ValueTask EnsureHostCapacityAsync()
    {
        Task<IAsyncDisposable> acquisition;
        lock (capacityGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (capacityAcquisition is null || capacityAcquisition.IsFaulted || capacityAcquisition.IsCanceled)
            {
                capacityAcquisition = CapacityBudget.EnterAsync().AsTask();
            }

            acquisition = capacityAcquisition;
        }

        await acquisition.ConfigureAwait(false);
        lock (capacityGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }

    private protected void EnsureHostCapacity() =>
        EnsureHostCapacityAsync().GetAwaiter().GetResult();

    public virtual ValueTask DisposeAsync()
    {
        lock (capacityGate)
        {
            disposed = true;
            return new ValueTask(capacityDisposal ??= ReleaseCapacityAsync(capacityAcquisition));
        }
    }

    private static async Task ReleaseCapacityAsync(Task<IAsyncDisposable>? acquisition)
    {
        if (acquisition is null || acquisition.IsFaulted || acquisition.IsCanceled)
        {
            return;
        }

        var slot = await acquisition.ConfigureAwait(false);
        await slot.DisposeAsync().ConfigureAwait(false);
    }
}
