using Mcg.AgentOrchestrator.Core;

public sealed class CreatedStagePolicyTests
{
    [Xunit.Theory]
    [Xunit.InlineData("GOAL_OPERATION_BLOCKED goal=abcdef12 operation=conductor:workspace-create reason=concurrent-acceptance-or-replacement",
        "", CreatedStageAction.Hold, 1, "workspace-create-evidence-lease",
        "GOAL_OPERATION_BLOCKED goal=abcdef12 operation=conductor:workspace-create reason=concurrent-acceptance-or-replacement")]
    [Xunit.InlineData("", @"C:\tmp\workspace", CreatedStageAction.Proceed, 0, "created-proceed", @"Workspace created: C:\tmp\workspace")]
    public void EachOutcome_PreservesActionEvidenceAndReason_AndReplays(
        string message, string path, CreatedStageAction action, int rung, string evidence, string reason)
    {
        var facts = new CreatedStageFacts { LeaseUnavailableMessage = message, CreatedPath = path };
        var recorded = facts.ToRecordedFacts();
        Assert.Equal(2, recorded.Count);
        Assert.Equal("lease-unavailable-message", recorded[0].Name);
        Assert.Equal(message, recorded[0].Value);
        Assert.Equal("created-path", recorded[1].Name);
        Assert.Equal(path, recorded[1].Value);
        AssertDecision(CreatedStagePolicy.Evaluate(facts), action, rung, evidence, reason);
        AssertDecision(CreatedStagePolicy.Evaluate(CreatedStageFacts.FromRecordedFacts(recorded)), action, rung, evidence, reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("count")]
    [Xunit.InlineData("duplicate")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("null-value")]
    public void Replay_RejectsMalformedFacts(string defect)
    {
        var facts = new CreatedStageFacts().ToRecordedFacts().ToList();
        switch (defect)
        {
            case "count": facts.RemoveAt(0); break;
            case "duplicate": facts[1] = facts[0]; break;
            case "missing": facts[1] = new("unknown", ""); break;
            case "null-value": facts[1] = new("created-path", null!); break;
        }
        Assert.Throws<InvalidOperationException>(() => CreatedStageFacts.FromRecordedFacts(facts));
    }

    [Xunit.Fact]
    public void NullInput_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => CreatedStagePolicy.Evaluate(null!));
        Assert.Throws<ArgumentNullException>(() => CreatedStageFacts.FromRecordedFacts(null!));
    }

    private static void AssertDecision(CreatedStageDecision actual, CreatedStageAction action, int rung, string evidence, string reason)
    {
        Assert.Equal(action, actual.Action);
        Assert.Equal(rung, actual.DiscriminatingRung);
        Assert.Equal(evidence, actual.DiscriminatingEvidence);
        Assert.Equal(reason, actual.Reason);
        var record = actual.ToRecord();
        Assert.Equal("created", record.Stage);
        Assert.Equal(action.ToString(), record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(reason, record.Reason);
    }
}
