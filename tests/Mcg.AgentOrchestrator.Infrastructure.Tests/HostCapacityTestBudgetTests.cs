public sealed class HostCapacityTestBudgetTests
{
    // A hang guard, not pacing: every wait below is released by an explicit signal, so a
    // healthy budget completes immediately and only a broken one reaches this bound.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Xunit.Fact(DisplayName = "Host_capacity_budget_admits_its_slots_concurrently")]
    public async Task HostCapacityBudgetAdmitsItsSlotsConcurrently()
    {
        var budget = new HostCapacityTestBudget(2);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task HoldAsync(TaskCompletionSource entered)
        {
            await using var slot = await budget.EnterAsync();
            entered.SetResult();
            await release.Task;
        }

        var first = HoldAsync(firstEntered);
        var second = HoldAsync(secondEntered);

        // Both entry signals arrive before either holder is allowed to leave, so the two
        // operations are provably inside the budget at the same time.
        await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(HangGuard);
        Assert.Equal(0, budget.AvailableSlots);

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(HangGuard);
        Assert.Equal(2, budget.AvailableSlots);
    }

    [Xunit.Fact(DisplayName = "Host_capacity_budget_holds_the_next_entrant_until_a_slot_is_released")]
    public async Task HostCapacityBudgetHoldsTheNextEntrantUntilASlotIsReleased()
    {
        var budget = new HostCapacityTestBudget(1);
        var held = await budget.EnterAsync();
        var queued = budget.EnterAsync().AsTask();

        // This budget is local to the test, so nothing else can release it. The queued
        // entrant's incompleteness is a decided fact here, not a timing sample.
        Assert.Equal(0, budget.AvailableSlots);
        Assert.False(queued.IsCompleted);

        await held.DisposeAsync();
        await using var admitted = await queued.WaitAsync(HangGuard);
        Assert.Equal(0, budget.AvailableSlots);
    }

    [Xunit.Fact(DisplayName = "Host_capacity_bound_fixture_takes_and_returns_exactly_one_slot")]
    public async Task HostCapacityBoundFixtureTakesAndReturnsExactlyOneSlot()
    {
        var budget = new HostCapacityTestBudget(2);
        var fixture = new ProbeFixture(budget);

        await fixture.InitializeAsync();
        Assert.Equal(1, budget.AvailableSlots);

        await fixture.DisposeAsync();
        Assert.Equal(2, budget.AvailableSlots);

        // Disposal is idempotent: a second call must not hand back a slot it never took.
        await fixture.DisposeAsync();
        Assert.Equal(2, budget.AvailableSlots);
    }

    [Xunit.Fact(DisplayName = "Host_capacity_bound_fixture_that_ignores_the_budget_runs_unbounded")]
    public async Task HostCapacityBoundFixtureThatIgnoresTheBudgetRunsUnbounded()
    {
        // Negative control for the fixture contract. A subclass that overrides the lifetime
        // without taking a slot still initializes while the budget is exhausted, so
        // membership cannot be inferred from the base type alone: only fixtures that
        // actually enter are bounded. Deleting the base call must fail a capacity claim,
        // and this is the assertion that records it.
        var budget = new HostCapacityTestBudget(1);
        await using var held = await budget.EnterAsync();
        Assert.Equal(0, budget.AvailableSlots);

        var evader = new BudgetIgnoringProbeFixture(budget);
        await evader.InitializeAsync().AsTask().WaitAsync(HangGuard);

        Assert.True(evader.Initialized);
        Assert.Equal(0, budget.AvailableSlots);
        await evader.DisposeAsync();
        Assert.Equal(0, budget.AvailableSlots);
    }

    [Xunit.Theory(DisplayName = "Host_capacity_budget_derives_slots_from_the_host_or_the_override")]
    [Xunit.InlineData(null, 1, 2)]
    [Xunit.InlineData(null, 8, 2)]
    [Xunit.InlineData(null, 24, 3)]
    [Xunit.InlineData(null, 64, 4)]
    [Xunit.InlineData("", 24, 3)]
    [Xunit.InlineData("1", 24, 1)]
    [Xunit.InlineData(" 12 ", 24, 12)]
    public void HostCapacityBudgetDerivesSlotsFromTheHostOrTheOverride(
        string? configuredSlotCount,
        int processorCount,
        int expected) =>
        Assert.Equal(expected, HostCapacityTestBudget.ResolveSlotCount(configuredSlotCount, processorCount));

    [Xunit.Theory(DisplayName = "Host_capacity_budget_rejects_an_unusable_slot_override")]
    [Xunit.InlineData("0")]
    [Xunit.InlineData("-1")]
    [Xunit.InlineData("many")]
    [Xunit.InlineData("2.5")]
    public void HostCapacityBudgetRejectsAnUnusableSlotOverride(string configuredSlotCount)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => HostCapacityTestBudget.ResolveSlotCount(configuredSlotCount, 24));
        Assert.Contains(HostCapacityTestBudget.SlotCountVariable, error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Host_capacity_budget_rejects_a_nonpositive_slot_count")]
    public void HostCapacityBudgetRejectsANonpositiveSlotCount() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostCapacityTestBudget(0));

    private sealed class ProbeFixture(HostCapacityTestBudget budget) : HostCapacityBoundTestBase
    {
        private protected override HostCapacityTestBudget CapacityBudget { get; } = budget;
    }

    private sealed class BudgetIgnoringProbeFixture(HostCapacityTestBudget budget) : HostCapacityBoundTestBase
    {
        private protected override HostCapacityTestBudget CapacityBudget { get; } = budget;

        public bool Initialized { get; private set; }

        public override ValueTask InitializeAsync()
        {
            Initialized = true;
            return ValueTask.CompletedTask;
        }
    }
}
