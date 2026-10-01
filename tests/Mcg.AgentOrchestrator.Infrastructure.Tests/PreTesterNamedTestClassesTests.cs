using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class PreTesterNamedTestClassesTests
{
    [Fact]
    public void HarvestKeepsFirstAppearanceAndDropsDottedSuffixes()
    {
        var names = PreTesterNamedTestClasses.Harvest(
            "`BriefNamedRegressionTests.SomeMethod` " +
            "tests/Example/PlannerNamedNeighborTests.cs " +
            "BriefNamedRegressionTests.MethodTests Deferred_2Tests " +
            "lowercaseTests BriefHelper 2InvalidTests _InvalidTests");

        Assert.Equal(["BriefNamedRegressionTests", "PlannerNamedNeighborTests", "Deferred_2Tests"], names);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain prose without test names")]
    public void HarvestEmptyTextContributesNothing(string? text)
        => Assert.Empty(PreTesterNamedTestClasses.Harvest(text));

    [Fact]
    public void NamedResolutionRequiresOneFileEvenWithinOneProject()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var project = Path.Combine(root, "tests", "Example.Tests");
            Directory.CreateDirectory(Path.Combine(project, "nested"));
            File.WriteAllText(Path.Combine(project, "Example.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, "DuplicateTests.cs"), "class DuplicateTests {}");
            File.WriteAllText(Path.Combine(project, "nested", "DuplicateTests.cs"), "class DuplicateTests {}");

            Assert.Empty(DeveloperDeferredTestSelections.ResolveNames(
                root, ["DuplicateTests"], requireUniqueSourceFile: true).Selections);
            Assert.Single(DeveloperDeferredTestSelections.Resolve(root, "deferred - DuplicateTests").Selections);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
