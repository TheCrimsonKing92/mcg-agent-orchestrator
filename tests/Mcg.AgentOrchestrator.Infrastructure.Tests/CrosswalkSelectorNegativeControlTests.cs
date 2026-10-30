using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only immutable input fixtures, with no process or ambient state.
public sealed class CrosswalkSelectorNegativeControlTests
{
    [Theory]
    [InlineData("spec/a_spec.lua::first", 0)]
    [InlineData("spec/a_spec.lua", -1)]
    [InlineData("./spec/a_spec.lua", -1)]
    [InlineData("spec\\a_spec.lua", -1)]
    [InlineData("a_spec.lua", 2)]
    [InlineData("src/game/types/test.test.ts", 3)]
    public void ExactAndFileSelectors_PreserveExpansionAndGrading(string selector, int singleIndex)
    {
        string[] ids = ["spec/a_spec.lua::first", "spec/a_spec.lua::second", "a_spec.lua::root", "src/game/types/test.test.ts::case"];
        var expected = singleIndex < 0 ? new[] { ids[0], ids[1] } : [ids[singleIndex]];
        Assert.Equal(expected, CrosswalkFocusedEvidence.ExpandSelectors([selector], ids));
        var records = new NormalizedTestResultSource().ReadJson(CrosswalkAcceptanceFixture.Report(
            ids.Select(id => (id, "passed")).ToArray())).Records;
        Assert.Equal(CrosswalkFocusedEvidence.SelectionGrade.Satisfied, CrosswalkFocusedEvidence.Grade(records, selector));
    }

    [Theory]
    [InlineData("spec/a_spec.lua::Suite", "spec/a_spec.lua::Suite child")]
    [InlineData("spec/a_spec.lua", "other.lua::spec/a_spec.lua child")]
    [InlineData("a_spec.lua", "other.lua::a_spec.lua child")]
    public void ExactAndFileSelectors_NeverFallBackToSuiteMatching(string selector, string misleadingId)
    {
        Assert.Empty(CrosswalkFocusedEvidence.ExpandSelectors([selector], [misleadingId]));
    }
}
