using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestCollectionGuardRulesTests
{
    private static readonly AcceptanceTestLane[] Lanes =
    [
        new("Dotnet build slots", "FullyQualifiedName~ConductorBatchLoopTests")
        {
            OwnedCollections = ["DotnetBuildSlots"]
        }
    ];

    [Fact]
    public void Owned_collection_without_lane_substring_reports_membership_change()
    {
        var descriptor = new AcceptanceTestClassDescriptor("ConductorLegacyHolderAdmissionTests", "DotnetBuildSlots");

        var violation = Assert.Single(TestCollectionGuardRules.FindLaneMembershipViolations(Lanes, [descriptor]));

        Assert.Equal("lane-membership", violation.Rule);
        Assert.Equal(descriptor.FullName, violation.ClassFullName);
        Assert.Equal(descriptor.Collection, violation.Collection);
        Assert.Empty(violation.BaselineLanes);
        Assert.Equal(["Dotnet build slots"], violation.ResolvedLanes);
        Assert.False(violation.BaselineLanes.SequenceEqual(violation.ResolvedLanes));
    }

    [Fact]
    public void Lane_member_without_isolated_collection_reports_violation()
    {
        var descriptor = new AcceptanceTestClassDescriptor("ConductorBatchLoopTestsLandingHeldReceiptRelease", null);

        var violation = Assert.Single(TestCollectionGuardRules.FindUnisolatedLaneClasses(
            Lanes, [descriptor], "Dotnet build slots", "DotnetBuildSlots", []));

        Assert.Equal("isolated-collection", violation.Rule);
        Assert.Equal(descriptor.FullName, violation.ClassFullName);
        Assert.Null(violation.Collection);
        Assert.Equal(["Dotnet build slots"], violation.ResolvedLanes);
    }

    [Theory]
    [InlineData("ConductorBatchLoopTestsExample", "DotnetBuildSlots")]
    [InlineData("QuokkaHarborUnmatchedProbe", null)]
    public void Correctly_placed_or_unmatched_class_passes_both_rules(string name, string? collection)
    {
        AcceptanceTestClassDescriptor[] descriptors = [new(name, collection)];

        Assert.Empty(TestCollectionGuardRules.FindLaneMembershipViolations(Lanes, descriptors));
        Assert.Empty(TestCollectionGuardRules.FindUnisolatedLaneClasses(
            Lanes, descriptors, "Dotnet build slots", "DotnetBuildSlots", []));
    }

    [Fact]
    public void Named_exemption_allows_unisolated_lane_member()
    {
        var descriptor = new AcceptanceTestClassDescriptor("ConductorBatchLoopTestsLandingHeldReceiptRelease", null);

        Assert.Empty(TestCollectionGuardRules.FindUnisolatedLaneClasses(
            Lanes, [descriptor], "Dotnet build slots", "DotnetBuildSlots", [descriptor.FullName]));
    }

    [Fact]
    public void Real_repository_satisfies_both_shared_rules()
    {
        var root = VerifiedRepositoryRoot.Find();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var descriptors = AcceptanceTestClassSourceScanner.Scan(root);
        var exemptions = TestCollectionGuardRules.LoadIsolatedCollectionExemptions(root);

        Assert.NotEmpty(descriptors);
        Assert.Contains(lanes, lane => lane.Name == "Dotnet build slots");
        Assert.Empty(TestCollectionGuardRules.FindLaneMembershipViolations(lanes, descriptors));
        Assert.Empty(TestCollectionGuardRules.FindUnisolatedLaneClasses(
            lanes, descriptors, "Dotnet build slots", "DotnetBuildSlots", exemptions.Keys));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"class\":\"\",\"reason\":\"why\"}]")]
    [InlineData("[{\"class\":\"Probe\",\"reason\":\" \"}]")]
    [InlineData("[{\"class\":3,\"reason\":\"why\"}]")]
    [InlineData("[{\"class\":\"Probe\",\"reason\":\"why\"},{\"class\":\"Probe\",\"reason\":\"again\"}]")]
    public void Invalid_exemption_data_fails_with_file_path(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-collection-guards", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        try
        {
            var path = Path.Combine(root, TestCollectionGuardRules.ExemptionsRelativePath);
            File.WriteAllText(path, json);

            var exception = Assert.Throws<InvalidDataException>(() =>
                TestCollectionGuardRules.LoadIsolatedCollectionExemptions(root));

            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
