using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class DeveloperDeferredTestSelectionTests
{
    [Fact]
    public void ResolveSelectsOnlyDeclaredClassesAndSkipsGeneratedTrees()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var project = Path.Combine(root, "tests", "Example.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Example.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, "DeclaredTests.cs"),
                "public class DeclaredTests {}");
            File.WriteAllText(Path.Combine(project, "mixedcasetests.cs"),
                "public class MixedCaseTests {}");
            var generated = Path.Combine(project, "obj");
            Directory.CreateDirectory(generated);
            File.WriteAllText(Path.Combine(generated, "GeneratedTests.cs"),
                "public class GeneratedTests {}");

            var selection = DeveloperDeferredTestSelections.Resolve(root,
                "deferred - DeclaredTests, MissingTests; Final Infrastructure Tests ConductorDriverTests");

            Assert.Equal("DeclaredTests", Assert.Single(selection.Selections).TestClass);
            Assert.Equal("MissingTests", Assert.Single(selection.NotRun));
            Assert.DoesNotContain("ConductorDriverTests", selection.NotRun);

            var mixedCaseSelection = DeveloperDeferredTestSelections.Resolve(root,
                "deferred - MixedCaseTests");
            Assert.Equal("MixedCaseTests", Assert.Single(mixedCaseSelection.Selections).TestClass);

            var generatedSelection = DeveloperDeferredTestSelections.Resolve(root,
                "deferred - GeneratedTests");
            Assert.Empty(generatedSelection.Selections);
            Assert.Equal("GeneratedTests", Assert.Single(generatedSelection.NotRun));

            var wrappedSelection = DeveloperDeferredTestSelections.Resolve(root,
                "deferred - conductor to execute `DeclaredTests`, \"MixedCaseTests\", 'MissingTests'");
            Assert.Equal(["DeclaredTests", "MixedCaseTests"],
                wrappedSelection.Selections.Select(item => item.TestClass).ToArray());
            Assert.Equal("MissingTests", Assert.Single(wrappedSelection.NotRun));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("deferred - no named classes")]
    [InlineData("deferred - ConductorDriverTests appears only in prose")]
    public void ResolveDoesNotTreatProseAsDeclaration(string testsField)
    {
        var selection = DeveloperDeferredTestSelections.Resolve("unused", testsField);
        Assert.Empty(selection.Selections);
        Assert.Empty(selection.NotRun);
    }
}
