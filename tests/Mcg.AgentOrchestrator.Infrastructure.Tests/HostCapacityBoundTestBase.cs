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
    private IAsyncDisposable? capacitySlot;

    /// <summary>
    /// The budget this fixture draws from. Overridden only by the controls that verify the
    /// lifetime contract against an isolated budget instead of the shared process one.
    /// </summary>
    private protected virtual HostCapacityTestBudget CapacityBudget => HostCapacityTestBudget.Shared;

    public virtual async ValueTask InitializeAsync() =>
        capacitySlot = await CapacityBudget.EnterAsync();

    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref capacitySlot, null) is { } slot)
        {
            await slot.DisposeAsync();
        }
    }
}
