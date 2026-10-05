using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure classification with no shared state.
public sealed class ConductEventOperatorClassifierTestsRebaseAutoMerge
{
    [Theory]
    [InlineData("rebase-automerge", "REBASE_CONFLICT_AUTOMERGE goal=g result=merged", "outcome")]
    [InlineData("rebase-automerge", "REBASE_CONFLICT_AUTOMERGE goal=g result=refused", "decision")]
    [InlineData("rebase-automerge", "REBASE_CONFLICT_AUTOMERGE_EXTRA result=merged", null)]
    [InlineData("rebase-automerge", "REBASE_CONFLICT_AUTOMERGE result=merged-extra", null)]
    [InlineData("rebase-automerge", "REBASE_CONFLICT_AUTOMERGE reason=result=refused", null)]
    [InlineData("rebase-automerge", "REBASE_CONFLICT_AUTOMERGE result=unknown", null)]
    [InlineData("rebase-automerge", "REBASE_CONFLICT_AUTOMERGE result=merged result=refused", null)]
    [InlineData("other", "REBASE_CONFLICT_AUTOMERGE result=merged", null)]
    public void Classify_ExactDecisionEvidence_ReturnsOperatorClass(string kind, string detail, string? expected) =>
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify(kind, detail));
}
