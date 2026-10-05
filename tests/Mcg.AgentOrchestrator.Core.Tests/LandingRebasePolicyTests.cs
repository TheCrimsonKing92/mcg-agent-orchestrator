using Mcg.AgentOrchestrator.Core;

public sealed class LandingRebasePolicyTests
{
    [Theory]
    [InlineData("pre-landing", 0, LandingRebaseAction.Proceed, "rebase-updated-branch", "Rebase updated the goal branch.")]
    [InlineData("pre-merge", 0, LandingRebaseAction.Proceed, "rebase-updated-branch", "Rebase updated the goal branch.")]
    [InlineData("pre-landing", 1, LandingRebaseAction.Retire, "missing-branch-retired", "Conductor tick retired missing goal branch before landing because the goal artifact could not be rebased: branch is gone")]
    [InlineData("pre-merge", 1, LandingRebaseAction.Retire, "missing-branch-retired", "Conductor tick retired missing goal branch before landing because the goal artifact could not be rebased: branch is gone")]
    [InlineData("pre-landing", 2, LandingRebaseAction.Escalate, "rebase-conflict", "pre-landing rebase conflict (src/A.cs, src/B.cs); use 'workspace rebase' to resolve")]
    [InlineData("pre-merge", 2, LandingRebaseAction.Escalate, "rebase-conflict", "pre-merge rebase conflict (src/A.cs, src/B.cs); use 'workspace rebase' to resolve")]
    [InlineData("pre-landing", 3, LandingRebaseAction.Escalate, "rebase-failed", "pre-landing rebase failed: git exit 128")]
    [InlineData("pre-merge", 3, LandingRebaseAction.Escalate, "rebase-failed", "pre-merge rebase failed: git exit 128")]
    public void BothPhasesPreserveExactDisposition(string phase, int rung, LandingRebaseAction action, string evidence, string reason)
    {
        foreach (var applySideEffects in new[] { false, true })
        {
            var decision = LandingRebasePolicy.Evaluate(FactsForRung(phase, rung) with { ApplySideEffects = applySideEffects });
            Assert.Equal(action, decision.Action);
            Assert.Equal(rung, decision.DiscriminatingRung);
            Assert.Equal(evidence, decision.DiscriminatingEvidence);
            Assert.Equal(reason, decision.Reason);
            var recorded = decision.ToRecord();
            Assert.Equal("landing-rebase", recorded.Stage);
            Assert.Equal(action.ToString(), recorded.Action);
            Assert.Equal(rung, recorded.Rung);
            Assert.Equal(evidence, recorded.DiscriminatingEvidence);
            Assert.Equal(reason, recorded.Reason);
            Assert.Equal(6, recorded.Facts.Count);
            Assert.Equal(applySideEffects ? "true" : "false", Assert.Single(recorded.Facts, f => f.Name == "applySideEffects").Value);
        }
    }

    public static LandingRebaseFacts FactsForRung(string phase, int rung) => rung switch
    {
        0 => new(phase, true, "Rebased", [], "updated", true),
        1 => new(phase, false, "MissingBranch", [], "branch is gone", true),
        2 => new(phase, false, "Conflict", ["src/A.cs", "src/B.cs"], "conflicts", true),
        3 => new(phase, false, "Failed", [], "git exit 128", true),
        _ => throw new ArgumentOutOfRangeException(nameof(rung))
    };

    [Theory]
    [InlineData("phase", "post-merge")]
    [InlineData("updatedBranch", "TRUE")]
    [InlineData("applySideEffects", "")]
    public void MalformedRecordedFactsFailLoudly(string name, string value)
    {
        var recorded = FactsForRung("pre-merge", 2).ToRecordedFacts().Select(f => f.Name == name ? new PolicyDecisionFact(name, value) : f).ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingRebaseFacts.FromRecordedFacts(recorded));
    }

    [Fact]
    public void IncompleteDuplicateAndMisnamedFactsCannotReplay()
    {
        var recorded = FactsForRung("pre-merge", 2).ToRecordedFacts().ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingRebaseFacts.FromRecordedFacts(recorded[..^1]));
        recorded[^1] = recorded[0];
        Assert.Throws<InvalidOperationException>(() => LandingRebaseFacts.FromRecordedFacts(recorded));
        recorded[^1] = new("unknown", "");
        Assert.Throws<InvalidOperationException>(() => LandingRebaseFacts.FromRecordedFacts(recorded));
        Assert.Throws<InvalidOperationException>(() => LandingRebasePolicy.Evaluate(FactsForRung("post-merge", 2)));
    }
}
