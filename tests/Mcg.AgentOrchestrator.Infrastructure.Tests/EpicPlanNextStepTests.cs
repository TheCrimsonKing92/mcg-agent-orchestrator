using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: pure next-step projections with no shared state or IO.
public sealed class EpicPlanNextStepTests
{
    [Fact]
    public void InFlightAndVerified_PrecedeReadyAndSteps_InPlanOrder()
    {
        var result = EpicPlanNextStep.Compute([Step(1, false), Slice(2, EpicPlanSliceStatus.Ready),
            Slice(4, EpicPlanSliceStatus.Verified), Slice(3, EpicPlanSliceStatus.InFlight)]);
        Assert.Equal("3. slice3 — In flight (goal Active); 4. slice4 — Verified", result.Text);
        Assert.False(result.IsStalled);
    }

    [Fact]
    public void Ready_UsesFirstInPlanOrder_SkippingBlocked()
    {
        var result = EpicPlanNextStep.Compute([Slice(1, EpicPlanSliceStatus.Blocked),
            Slice(3, EpicPlanSliceStatus.Ready), Step(4, false), Slice(2, EpicPlanSliceStatus.Ready)]);
        Assert.Equal("2. slice2 — Ready", result.Text);
        Assert.False(result.IsStalled);
    }

    [Fact]
    public void OpenStep_IsUsedWhenNoSliceCanRun()
    {
        var result = EpicPlanNextStep.Compute([Step(1, true), Slice(2, EpicPlanSliceStatus.Blocked), Step(3, false), Step(4, false)]);
        Assert.Equal("3. step3 — open", result.Text);
        Assert.False(result.IsStalled);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Parked")]
    [InlineData("Blocked")]
    [InlineData("Missing")]
    [InlineData("RemainingScope")]
    [InlineData("ReconciliationRequired")]
    [InlineData("Unresolved")]
    [InlineData("Claimed")]
    [InlineData("Landed")]
    public void IncompleteWithoutRunnableWork_IsStalled(string status)
    {
        var items = new[] { Slice(1, Enum.Parse<EpicPlanSliceStatus>(status)), Step(2, true) };
        var result = EpicPlanNextStep.Compute(items);
        Assert.True(result.IsStalled);
        Assert.Equal("Stalled: nothing in flight or ready", result.Text);
        Assert.Equal("0 of 1 slices landed; Stalled: nothing in flight or ready", EpicPlanNextStep.Summary(items));
    }

    [Fact]
    public void DoneAndSuperseded_AreComplete_EmptyIsNoPlan()
    {
        var items = new[] { Slice(1, EpicPlanSliceStatus.Done), Slice(2, EpicPlanSliceStatus.Done),
            Slice(3, EpicPlanSliceStatus.Superseded), Step(4, true) };
        Assert.Equal(new EpicPlanNextStepResult("complete", false), EpicPlanNextStep.Compute(items));
        Assert.Equal("2 of 2 slices landed; next: complete", EpicPlanNextStep.Summary(items));
        Assert.Equal(new EpicPlanNextStepResult("no plan", false), EpicPlanNextStep.Compute([]));
        Assert.Equal("0 of 0 slices landed; next: no plan", EpicPlanNextStep.Summary([]));
    }

    [Theory]
    [InlineData("Unknown", "outcome evidence required")]
    [InlineData("Unmet", "outcome unmet")]
    [InlineData("Proven", "complete")]
    public void Landed_scope_retains_counts_while_outcome_work_has_its_own_next_step(string state, string next)
    {
        var items = new[] { Slice(1, EpicPlanSliceStatus.Done), Step(2, true) };
        var outcome = new EpicOutcomeProjection(Enum.Parse<EpicOutcomeState>(state), 1, "Outcome", [], []);
        var view = new EpicPlanView(new("epic", null, items.Select(item => item.Item).ToArray(), []), items, [], outcome);
        Assert.Equal("1 of 1 slices landed; next: complete", EpicPlanNextStep.Summary(items));
        Assert.Equal(next, EpicPlanNextStep.Compute(view).Text);
    }

    private static EpicPlanItemStatus Slice(int position, EpicPlanSliceStatus status) =>
        new(new(position, EpicPlanItemKind.Slice, "backlog" + position, null, false), status, "slice" + position,
            status == EpicPlanSliceStatus.InFlight ? "goal Active" : null);
    private static EpicPlanItemStatus Step(int position, bool done) =>
        new(new(position, EpicPlanItemKind.Step, null, "step" + position, done), null, "step" + position);
}
