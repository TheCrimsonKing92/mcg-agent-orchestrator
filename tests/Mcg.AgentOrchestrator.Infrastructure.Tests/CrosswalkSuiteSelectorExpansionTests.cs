using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure selector resolution with in-memory discovery and results.
public sealed class CrosswalkSuiteSelectorExpansionTests
{
    [Fact]
    public void SuitePrefix_SelectsAcrossFilesWithOrdinalSpaceBoundary()
    {
        string[] ids = [
            "spec/pzis_string_utils_spec.lua::PZISStringUtils endsWith returns true",
            "spec/pzis_string_utils_spec.lua::PZISStringUtils trim strips",
            "spec/pzis_string_utils_extra_spec.lua::PZISStringUtilsExtra pad pads",
            "spec/pzis_string_utils_extra_spec.lua::PZISStringUtils split splits",
            "spec/pzis_string_utils_spec.lua::PZISStringUtils"
        ];
        Assert.Equal([ids[0], ids[1], ids[3], ids[4]], CrosswalkFocusedEvidence.ExpandSelectors(["PZISStringUtils"], ids));
        Assert.Empty(CrosswalkFocusedEvidence.ExpandSelectors(["pzisstringutils"], ids));
        Assert.Equal([ids[0], ids[1], ids[3], ids[4]],
            CrosswalkFocusedEvidence.ExpandSelectors(["PZISStringUtils", ids[0], "PZISStringUtils"], ids));
    }

    [Theory]
    [InlineData("passed", "Satisfied")]
    [InlineData("failed", "Failed")]
    [InlineData("error", "Failed")]
    [InlineData("skipped", "NotDemonstrated")]
    public void DottedSuite_GradesExecutedRecordsWithSameMatcher(string status, string expected)
    {
        const string id = "spec/pzis_collection_utils_spec.lua::PZISCollectionUtils.Set add adds a member";
        Assert.Equal([id], CrosswalkFocusedEvidence.ExpandSelectors(["PZISCollectionUtils.Set"], [id]));
        var records = new NormalizedTestResultSource().ReadJson(CrosswalkAcceptanceFixture.Report(
            (id, status), ("spec/other.lua::PZISCollectionUtils.SetExtra unrelated", "failed"))).Records;
        Assert.Equal(expected, CrosswalkFocusedEvidence.Grade(records, "PZISCollectionUtils.Set").ToString());
    }
}
