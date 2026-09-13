using System.Globalization;

/// <summary>
/// Bounds how many host-capacity-bound tests execute at once inside one test process.
///
/// The fixtures that take a slot drive real git subprocesses, real worktrees, SQLite
/// state files and build-permit directories. Per-operation ownership makes those tests
/// independent — distinct repositories, build storage roots, attention stores and
/// process registries — but independence is not free: their cost is host CPU, disk and
/// process capacity, which no amount of isolation partitions. The acceptance gate runs
/// several test processes concurrently, so an unbounded per-process degree of
/// parallelism multiplies into host oversubscription that inflates every test's wall
/// clock without reducing lane wall clock.
///
/// This is a capacity budget, not a correctness lock. It never orders two operations
/// against each other and never protects shared state. A fixture that needs exclusion
/// from process-global state still declares a nonparallel test collection.
/// </summary>
public sealed class HostCapacityTestBudget
{
    /// <summary>
    /// Pins the slot count instead of deriving it from the host. An operator measuring
    /// lane timing sets this to hold the degree of parallelism fixed across runs.
    /// </summary>
    public const string SlotCountVariable = "MCG_TEST_HOST_CAPACITY_SLOTS";

    private readonly SemaphoreSlim slots;

    public HostCapacityTestBudget(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        SlotCount = slotCount;
        slots = new SemaphoreSlim(slotCount, slotCount);
    }

    public static HostCapacityTestBudget Shared { get; } = new(ResolveSlotCount(
        Environment.GetEnvironmentVariable(SlotCountVariable),
        Environment.ProcessorCount));

    public int SlotCount { get; }

    /// <summary>Slots not currently held. Diagnostic only; it can change immediately.</summary>
    public int AvailableSlots => slots.CurrentCount;

    /// <summary>
    /// Waits for a slot and returns the handle that releases it. The wait is asynchronous
    /// so a parked fixture returns its runner thread instead of holding one idle.
    /// </summary>
    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Slot(slots);
    }

    /// <summary>
    /// One slot per eight logical processors, clamped to [2, 4]. Two keeps overlap real on
    /// a small host; four caps a large host so the several gate shards that run at once,
    /// each holding its own budget, still fit inside the machine.
    /// </summary>
    public static int ResolveSlotCount(string? configuredSlotCount, int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processorCount, 1);
        if (string.IsNullOrWhiteSpace(configuredSlotCount))
        {
            return Math.Clamp(processorCount / 8, 2, 4);
        }

        if (!int.TryParse(
                configuredSlotCount.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var configured) ||
            configured < 1)
        {
            throw new InvalidOperationException(
                $"{SlotCountVariable} must be a positive integer; found '{configuredSlotCount}'.");
        }

        return configured;
    }

    private sealed class Slot(SemaphoreSlim slots) : IAsyncDisposable
    {
        private SemaphoreSlim? held = slots;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref held, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
