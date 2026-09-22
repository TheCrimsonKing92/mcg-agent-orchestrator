using System.Globalization;

/// <summary>
/// Bounds how many host-capacity-bound tests execute at once inside one test process to a
/// chosen slot count: one slot per eight logical processors, clamped, or the value of
/// <see cref="SlotCountVariable"/>.
///
/// The fixtures that take a slot drive real git subprocesses, real worktrees, SQLite
/// state files and build-permit directories. Per-operation ownership already makes those
/// tests independent — distinct repositories, build storage roots, attention stores and
/// process registries — so the slot count bounds concurrent host-capacity use and is not
/// an isolation mechanism.
///
/// The slot count is a chosen bound, not a measured host threshold. Its cost and benefit
/// are to be measured by the operator-owned criterion 6 before/after comparison; see
/// docs/acceptance-gate-resource-isolation.md.
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
    /// One slot per eight logical processors, clamped to [2, 4]. The ratio and both clamp
    /// endpoints are chosen bounds, not measured host limits: the lower endpoint keeps two
    /// tests overlapping on a small host, the upper endpoint is the chosen ceiling.
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
