using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class FindingEvidenceNameSelectorTests
{
    [Xunit.Theory]
    [Xunit.InlineData(
        "Name~RecorderConstruction_AllCliSites_SupplyDefaultTransfer",
        "FullyQualifiedName~RecorderConstruction_AllCliSites_SupplyDefaultTransfer")]
    [Xunit.InlineData("  Name~Alpha  ", "  FullyQualifiedName~Alpha  ")]
    [Xunit.InlineData(
        "(FullyQualifiedName~ClassA&Name~MethodB)|Name~MethodC",
        "(FullyQualifiedName~ClassA&FullyQualifiedName~MethodB)|FullyQualifiedName~MethodC")]
    public void RewritesNameOperandsPreservingAllOtherCharacters(string filter, string expected)
    {
        Assert.Equal(expected, FindingEvidenceNameSelector.ToFullyQualified(filter));
    }

    [Xunit.Theory]
    [Xunit.InlineData("FullyQualifiedName~ClassA")]
    [Xunit.InlineData("DisplayName~MethodB")]
    [Xunit.InlineData("Name!~MethodB")]
    [Xunit.InlineData("Category!=HostIntegration")]
    [Xunit.InlineData("ClassName~MethodB")]
    [Xunit.InlineData("ConductorDriverTests")]
    [Xunit.InlineData("1Name~MethodB")]
    [Xunit.InlineData("_Name~MethodB")]
    [Xunit.InlineData("name~MethodB")]
    [Xunit.InlineData("NAME~MethodB")]
    [Xunit.InlineData("")]
    public void LeavesOtherSelectorsAndIdentifierSuffixesUnchanged(string filter)
    {
        Assert.Equal(filter, FindingEvidenceNameSelector.ToFullyQualified(filter));
    }
}
