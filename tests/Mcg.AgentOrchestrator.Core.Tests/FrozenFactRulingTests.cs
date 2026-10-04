using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class FrozenFactRulingTests
{
    [Fact]
    public void RulingOneRoundTripsEveryFieldAndRejectsOriginalFreeText()
    {
        var ruling = RulingOne();
        var rendered = ruling.Render();
        Assert.Equal("Frozen-fact ruling v1", rendered.Split(Environment.NewLine)[0]);
        var parsed = Assert.IsType<FrozenFactRuling>(FrozenFactRuling.TryParse(rendered));
        Assert.Equal(ruling.FrozenClasses, parsed.FrozenClasses);
        Assert.Equal(ruling.AmendedFacts, parsed.AmendedFacts);
        Assert.Equal(ruling.Basis, parsed.Basis);
        Assert.Equal(ruling.Unmodified, parsed.Unmodified);
        Assert.Equal(ruling.DiffCheck, parsed.DiffCheck);
        Assert.Equal(ruling.EvidenceReferences, parsed.EvidenceReferences);
        Assert.Equal(rendered, parsed.Render());
        Assert.Null(FrozenFactRuling.TryParse(
            "The fact builds the Verified, unmerged, no-gate-receipt shape the goal suppresses. " +
            "Only the attention block changed: Assert.Single(attention) and its four item assertions " +
            "became Assert.Empty(attention). Every SWEEP_BLOCKER assertion and the final " +
            "GoalWorktrees.TryResolve NotNull check stayed unmodified."));
    }

    [Fact]
    public void ValuesCollapseToSingleLinesAndLeadingBlankLinesAreAccepted()
    {
        var ruling = RulingOne() with { Basis = "Verified,\r\nunmerged,\nno-gate-receipt shape" };
        var parsed = Assert.IsType<FrozenFactRuling>(FrozenFactRuling.TryParse("\n\r\n" + ruling.Render()));
        Assert.Equal("Verified, unmerged, no-gate-receipt shape", parsed.Basis);
        Assert.Equal(ruling.Render(), parsed.Render());
    }

    [Theory]
    [InlineData("Basis: duplicate")]
    [InlineData("Unknown: value")]
    [InlineData("unlabelled text")]
    public void MalformedOrDuplicateFieldsAreRejected(string extra)
    {
        Assert.Null(FrozenFactRuling.TryParse(RulingOne().Render() + Environment.NewLine + extra));
    }

    internal static FrozenFactRuling RulingOne() => new(
        ["CliCommandTestsTerminalSweepCommands", "ReconcileSweepPendingGateEscalationTests", "ReconcileSweepRemediationTests"],
        [new("CliCommandTestsTerminalSweepCommands.TerminalGoalSweepNextAndConductSurfaceSameUnmergedBranchBlocker",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.TerminalSweepCommands.cs",
            "Change only the attention block from Assert.Single(attention) and its four item assertions to Assert.Empty(attention).")],
        "The fact builds the Verified, unmerged, no-gate-receipt shape the objective deliberately suppresses.",
        "Every SWEEP_BLOCKER assertion on next, conduct and diagnostics and the final GoalWorktrees.TryResolve NotNull check " +
        "remain unmodified, as do every other fact in CliCommandTestsTerminalSweepCommands, " +
        "ReconcileSweepPendingGateEscalationTests and ReconcileSweepRemediationTests.",
        "Run git diff -- tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.TerminalSweepCommands.cs; " +
        "every changed line must belong to the attention block inside the named fact.",
        ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.TerminalSweepCommands.cs:965-973",
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorItems.cs:33"]);
}
