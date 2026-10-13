using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: pure projections use an injected receipt validator and fixed observation time.
public sealed class EpicOutcomeEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static EpicOutcomeRequirement Requirement(string id = "service") => new(id, "Observe behavior", "file-sha256", "owner");
    private static EpicOutcomeRevision Revision(long version = 1) => new(version, "Outcome", [Requirement()], "defined", "owner", "cli", Now);
    private static EpicOutcomeObservation Observation(string id = "receipt", long version = 1, bool met = true,
        IReadOnlyList<string>? supersedes = null) => new(id, version, "service", 1, met, Now,
        new("file-sha256", "receipt-path", "owner", new string('a', 64)), supersedes ?? [], "snapshot", "owner", OperatorActorKind.Human, "cli", Now);

    [Fact]
    public void Empty_bars_and_new_versions_do_not_inherit_proof()
    {
        Assert.Equal(EpicOutcomeState.Unknown, EpicOutcomeEvaluator.Evaluate(null, _ => null).State);
        var legacy = new EpicOutcomeHistory([Revision() with { Requirements = [] }], []);
        Assert.Contains("no-definition", EpicOutcomeEvaluator.Evaluate(legacy, _ => null).Reasons);
        var stale = EpicOutcomeEvaluator.Evaluate(new([Revision(), Revision(2)], [Observation()]), _ => null);
        Assert.Equal(EpicOutcomeState.Unknown, stale.State);
        Assert.Equal(2, stale.BarVersion);
        Assert.Contains("stale-version", stale.Reasons);
        Assert.Contains("unevaluated", stale.Requirements[0].Reasons);
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("inaccessible-evidence")]
    [InlineData("receipt-identity-mismatch")]
    public void Invalid_receipt_never_establishes_proof(string problem)
    {
        var calls = 0;
        var outcome = EpicOutcomeEvaluator.Evaluate(new([Revision()], [Observation()]), observation =>
        {
            calls++;
            Assert.Equal("receipt", observation.ReceiptId);
            return problem;
        });
        Assert.Equal(1, calls);
        Assert.Equal(EpicOutcomeState.Unknown, outcome.State);
        Assert.Contains(problem, outcome.Reasons);
    }

    [Fact]
    public void Multiple_active_receipts_remain_ambiguous_until_all_are_explicitly_superseded()
    {
        var old = Observation("old", met: false);
        var convenient = Observation("convenient") with { Sequence = 2 };
        var ambiguous = EpicOutcomeEvaluator.Evaluate(new([Revision()], [old, convenient]), _ => null);
        Assert.Equal(EpicOutcomeState.Unknown, ambiguous.State);
        Assert.Contains("ambiguous-provenance", ambiguous.Reasons);
        var resolved = Observation("resolved", supersedes: ["old", "convenient"]) with { Sequence = 3 };
        var proven = EpicOutcomeEvaluator.Evaluate(new([Revision()], [old, convenient, resolved]), _ => null);
        Assert.Equal(EpicOutcomeState.Proven, proven.State);
        Assert.Equal(["resolved"], proven.Requirements[0].ReceiptIds);
        Assert.Equal("owner", Assert.Single(proven.Requirements[0].Evidence).Source.Authority);
        Assert.Equal("owner", proven.Requirements[0].Evidence[0].Actor);
    }

    [Fact]
    public void Unmet_requirement_remains_visible_alongside_unevaluated_requirements()
    {
        var outcome = EpicOutcomeEvaluator.Evaluate(new([Revision() with { Requirements = [Requirement(), Requirement("other")] }],
            [Observation(met: false)]), _ => null);
        Assert.Equal(EpicOutcomeState.Unmet, outcome.State);
        Assert.Contains("unevaluated", outcome.Reasons);
        Assert.Equal(EpicOutcomeState.Unknown, outcome.Requirements.Single(requirement => requirement.Id == "other").State);
    }

    [Fact]
    public void Proven_outcome_never_conceals_reopened_scope_or_unplanned_active_work()
    {
        var proven = EpicOutcomeEvaluator.Evaluate(new([Revision()], [Observation()]), _ => null);
        var reopened = new EpicPlanItemStatus(new(1, EpicPlanItemKind.Slice, "scope", null, false),
            EpicPlanSliceStatus.RemainingScope, "Scope");
        var plan = new EpicPlanView(new("epic", null, [reopened.Item], []), [reopened], [], proven);
        Assert.Equal(EpicPlanNextStep.StalledText, EpicPlanNextStep.Compute(plan).Text);
        Assert.StartsWith("0 of 1 slices landed;", EpicPlanNextStep.Summary(plan.Items));
        var active = new PortfolioEpicMember("epic", PortfolioMemberKind.Goal, "unplanned", Now, "owner");
        var unplanned = plan with { Items = [], UnplannedMembers = [active], UnplannedActiveMembers = [active] };
        Assert.Equal("unplanned active work requires reconciliation", EpicPlanNextStep.Compute(unplanned).Text);
        Assert.True(EpicPlanNextStep.Compute(unplanned).IsStalled);
    }
}
