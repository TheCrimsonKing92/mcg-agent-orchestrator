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
    private readonly Func<string, string?> environment;
    private readonly TimeProvider clock;

    public HostCapacityTestBudget(int slotCount) : this(slotCount, Environment.GetEnvironmentVariable) { }

    public HostCapacityTestBudget(int slotCount, Func<string, string?> environment)
        : this(slotCount, environment, TimeProvider.System) { }

    public HostCapacityTestBudget(int slotCount, Func<string, string?> environment, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        this.environment = environment;
        this.clock = clock;
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
        var started = clock.GetTimestamp();
        var destination = environment(HostCapacitySlotTimingWriter.VariableName);
        var testName = string.IsNullOrEmpty(destination) ? null : HostCapacitySlotTimingWriter.CurrentTestName();
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var acquired = clock.GetTimestamp();
        return new Slot(slots, destination, testName, started, acquired, SlotCount, clock);
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

    private sealed class Slot(SemaphoreSlim slots, string? destination, string? testName,
        long started, long acquired, int slotCount, TimeProvider clock) : IAsyncDisposable
    {
        private SemaphoreSlim? held = slots;

        public ValueTask DisposeAsync()
        {
            var disposed = clock.GetTimestamp();
            var released = Interlocked.Exchange(ref held, null);
            if (released is not null)
            {
                released.Release();
                if (!string.IsNullOrEmpty(destination))
                    HostCapacitySlotTimingWriter.TryAppend(destination, testName,
                        clock.GetElapsedTime(started, acquired).TotalMilliseconds,
                        clock.GetElapsedTime(acquired, disposed).TotalMilliseconds, slotCount);
            }
            return ValueTask.CompletedTask;
        }
    }
}
