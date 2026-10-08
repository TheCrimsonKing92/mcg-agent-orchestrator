using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns a fixture copy; no tools or process-global state are used.
public sealed class DotnetProjectDiscoveryNonTestUnitTests
{
    private const string Library = "src/Plain.Library/Plain.Library.csproj";
    private const string Tests = "tests/Plain.Tests/Plain.Tests.csproj";

    [Fact(DisplayName = "A marker-free library is confidently non-test without test setup or owner questions")]
    public void PlainLibraryHasNoTestSetupOrQuestions()
    {
        using var fixture = new ProjectOnboardingFixture("plain-library");
        var root = Path.Combine(fixture.Root, "sample-repo");
        var adapter = new DotnetProjectDiscoveryAdapter();
        var model = adapter.Discover(root);

        Assert.Equal(new[] { Library, Tests }, model.Units.Select(unit => unit.Id));
        var library = model.Units.Single(unit => unit.Id == Library);
        Assert.False(library.IsTest.Value);
        Assert.Equal(FactConfidence.High, library.IsTest.Confidence);
        Assert.Equal(new FactSource(Library, 1), library.IsTest.Source);
        Assert.True(File.Exists(Path.Combine(root, library.IsTest.Source.Path!)));
        Assert.DoesNotContain(model.TestSetups, setup => setup.UnitId == Library);
        Assert.DoesNotContain(model.OwnerQuestions, question =>
            question.FactKey.Contains(Library, StringComparison.Ordinal) || question.Question.Contains(Library, StringComparison.Ordinal));

        var testUnit = model.Units.Single(unit => unit.Id == Tests);
        Assert.True(testUnit.IsTest.Value);
        Assert.Equal(FactConfidence.High, testUnit.IsTest.Confidence);
        var setup = Assert.Single(model.TestSetups);
        Assert.Equal(Tests, setup.UnitId);
        Assert.Equal("xUnit", setup.Framework.Value);
        Assert.Equal("VSTest", setup.Runner.Value);
        Assert.Equal(FactConfidence.High, setup.Framework.Confidence);
        Assert.Equal(FactConfidence.High, setup.Runner.Confidence);
        Assert.All(new[] { testUnit.IsTest.Source, setup.Framework.Source, setup.Runner.Source }, source =>
        {
            Assert.Equal(Tests, source.Path);
            Assert.Contains("PackageReference", File.ReadAllLines(Path.Combine(root, source.Path!))[source.Line!.Value - 1]);
        });
        var edge = Assert.Single(model.Dependencies);
        Assert.Equal((Tests, Library), (edge.FromUnit, edge.ToUnit));
        Assert.Equal(FactConfidence.High, edge.Confidence);
        Assert.Equal(Tests, edge.Source.Path);
        Assert.Contains("ProjectReference", File.ReadAllLines(Path.Combine(root, Tests))[edge.Source.Line!.Value - 1]);
        Assert.Empty(model.OwnerQuestions);
        Assert.Equal(ProjectModelJson.Serialize(model), ProjectModelJson.Serialize(adapter.Discover(root)));
    }

    [Fact(DisplayName = "A conditional test package keeps unknown status and its original owner question")]
    public void ConditionalTestPackageRemainsUnknown()
    {
        using var fixture = new ProjectOnboardingFixture("plain-library");
        var root = Path.Combine(fixture.Root, "sample-repo");
        var adapter = new DotnetProjectDiscoveryAdapter();
        var markerFreeModel = adapter.Discover(root);
        var markerFreeStatus = markerFreeModel.Units.Single(unit => unit.Id == Library).IsTest;
        Assert.False(markerFreeStatus.Value);
        Assert.Equal(FactConfidence.High, markerFreeStatus.Confidence);
        Assert.Empty(markerFreeModel.OwnerQuestions);
        File.WriteAllText(Path.Combine(root, Library), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
              </ItemGroup>
            </Project>
            """);

        var model = adapter.Discover(root);
        var status = model.Units.Single(unit => unit.Id == Library).IsTest;
        Assert.Null(status.Value);
        Assert.Equal(FactConfidence.Low, status.Confidence);
        Assert.Equal(new FactSource(Library, 3), status.Source);
        var question = Assert.Single(model.OwnerQuestions.Where(question => question.FactKey == $"units/{Library}/isTest"));
        Assert.Equal(status.Source, question.Source);
        Assert.Equal($"units/{Library}/isTest: Is this unit a test unit? Its declarations do not establish an unconditional test status.", question.Question);
        var setup = model.TestSetups.Single(setup => setup.UnitId == Library);
        Assert.Equal("undetermined", setup.Framework.Value);
        Assert.Equal("undetermined", setup.Runner.Value);
        Assert.Equal(FactConfidence.Low, setup.Framework.Confidence);
        Assert.Equal(FactConfidence.Low, setup.Runner.Confidence);
    }

    [Theory(DisplayName = "Partial or conflicting markers keep the existing uncertain test status")]
    [InlineData("<PropertyGroup><IsTestProject Condition=\"'$(Configuration)' == 'Debug'\">true</IsTestProject></PropertyGroup>")]
    [InlineData("<ItemGroup><PackageReference Include=\"xunit.runner.visualstudio\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup>")]
    [InlineData("<ItemGroup><PackageReference Update=\"xunit\" /></ItemGroup>")]
    [InlineData("<Sdk Name=\"MSTest.Sdk\" Version=\"3.10.4\" />")]
    public void PartialOrConflictingMarkersRemainUnknown(string declarations)
    {
        using var fixture = new ProjectOnboardingFixture("plain-library");
        var root = Path.Combine(fixture.Root, "sample-repo");
        File.WriteAllText(Path.Combine(root, Library), $"<Project Sdk=\"Microsoft.NET.Sdk\">{declarations}</Project>");

        var model = new DotnetProjectDiscoveryAdapter().Discover(root);
        var status = model.Units.Single(unit => unit.Id == Library).IsTest;
        Assert.Null(status.Value);
        Assert.Equal(FactConfidence.Low, status.Confidence);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"units/{Library}/isTest");
        Assert.Contains(model.TestSetups, setup => setup.UnitId == Library);
    }
}
