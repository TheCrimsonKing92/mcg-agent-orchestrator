public sealed class AcceptanceLaneMembershipDiagnosticsTests
{
    [Xunit.Fact]
    public void MessageNamesClassCollectionLanesAndRepairRule()
    {
        var mismatch = new AcceptanceLaneMembershipMismatch(
            "QuokkaHarborSerializedFixture", "DotnetBuildSlots", ["Remainder"], ["Dotnet build slots"]);

        var message = AcceptanceLaneMembershipDiagnostics.Describe([mismatch]);

        Xunit.Assert.Contains("QuokkaHarborSerializedFixture", message);
        Xunit.Assert.Contains("collection=DotnetBuildSlots", message);
        Xunit.Assert.Contains("baseline=[Remainder]", message);
        Xunit.Assert.Contains("resolved=[Dotnet build slots]", message);
        Xunit.Assert.Contains("FullyQualifiedName~", message);
    }

    [Xunit.Fact]
    public void MessageNamesEveryOffenderInOneMessage()
    {
        var message = AcceptanceLaneMembershipDiagnostics.Describe(
            [
                new AcceptanceLaneMembershipMismatch("ZebraFixture", null, [], ["Remainder"]),
                new AcceptanceLaneMembershipMismatch("AardvarkFixture", "DotnetBuildSlots",
                    ["Remainder"], ["Dotnet build slots"])
            ], "ExampleGuard");

        Xunit.Assert.Contains("ExampleGuard found 2 offending test class(es):", message);
        Xunit.Assert.Contains("AardvarkFixture | collection=DotnetBuildSlots", message);
        Xunit.Assert.Contains("ZebraFixture | collection=(none) | baseline=[]", message);
        Xunit.Assert.True(message.IndexOf("AardvarkFixture", StringComparison.Ordinal) <
            message.IndexOf("ZebraFixture", StringComparison.Ordinal));
    }
}
