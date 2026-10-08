using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test owns its fixture directory; discovery never executes a tool.
public sealed class DotnetProjectDiscoveryAdapterTests
{
    private const string Library = "src/Sample.Library/Sample.Library.csproj";
    private const string Vstest = "tests/Sample.VstestTests/Sample.VstestTests.csproj";
    private const string Mtp = "tests/Sample.MtpTests/Sample.MtpTests.csproj";

    [Fact(DisplayName = "Solution discovery returns exact units, edges and declared test runners")]
    public void SolutionProducesExactGraphAndTestSetups()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        IProjectDiscoveryAdapter adapter = new DotnetProjectDiscoveryAdapter();
        var model = adapter.Discover(fixture.Root);

        Assert.Equal(new[] { Library, Mtp, Vstest }, model.Units.Select(unit => unit.Id));
        Assert.Equal(model.Units.Count, model.Units.Select(unit => unit.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { (Mtp, Library), (Vstest, Library) }, model.Dependencies.Select(edge => (edge.FromUnit, edge.ToUnit)));
        Assert.All(model.Dependencies, edge =>
        {
            Assert.Equal(FactConfidence.High, edge.Confidence);
            AssertSource(fixture.Root, edge.Source);
            Assert.Contains("ProjectReference", File.ReadAllLines(Path.Combine(fixture.Root, edge.Source.Path!))[edge.Source.Line!.Value - 1]);
        });
        Assert.All(model.Units, unit =>
        {
            Assert.Equal(FactConfidence.High, unit.Location.Confidence);
            AssertSource(fixture.Root, unit.Location.Source);
            AssertSource(fixture.Root, unit.Name.Source);
            AssertSource(fixture.Root, unit.IsTest.Source);
        });
        Assert.False(model.Units.Single(unit => unit.Id == Library).IsTest.Value);
        Assert.Equal(2, model.TestSetups.Count);
        Assert.Equal("VSTest", model.TestSetups.Single(setup => setup.UnitId == Vstest).Runner.Value);
        Assert.Equal("MTP", model.TestSetups.Single(setup => setup.UnitId == Mtp).Runner.Value);
        Assert.All(model.TestSetups, setup =>
        {
            Assert.Equal("xUnit", setup.Framework.Value);
            Assert.Equal(FactConfidence.High, setup.Framework.Confidence);
            Assert.Equal(FactConfidence.High, setup.Runner.Confidence);
            AssertSource(fixture.Root, setup.Framework.Source);
            AssertSource(fixture.Root, setup.Runner.Source);
        });
        Assert.Empty(model.OwnerQuestions);
        Assert.Equal(ProjectModelJson.Serialize(model), ProjectModelJson.Serialize(adapter.Discover(fixture.Root)));
    }

    [Fact(DisplayName = "Without a solution, each uncertain fact has exactly one owner question")]
    public void NoSolutionLeavesUnknownFrameworkAndRunnerUndetermined()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(2, model.Units.Count);
        var expectedKeys = new List<string>();
        foreach (var unit in model.Units)
        {
            Assert.Equal(FactConfidence.Low, unit.Location.Confidence);
            Assert.Equal(FactConfidence.Low, unit.Name.Confidence);
            AssertSource(fixture.Root, unit.Location.Source);
            expectedKeys.Add($"units/{unit.Id}/location");
            expectedKeys.Add($"units/{unit.Id}/name");
        }
        var setup = Assert.Single(model.TestSetups);
        Assert.Equal("undetermined", setup.Framework.Value);
        Assert.Equal("undetermined", setup.Runner.Value);
        Assert.NotEqual("VSTest", setup.Runner.Value);
        Assert.Equal(FactConfidence.Low, setup.Framework.Confidence);
        Assert.Equal(FactConfidence.Low, setup.Runner.Confidence);
        AssertSource(fixture.Root, setup.Framework.Source);
        AssertSource(fixture.Root, setup.Runner.Source);
        expectedKeys.Add($"tests/{setup.UnitId}/framework");
        expectedKeys.Add($"tests/{setup.UnitId}/runner");
        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), model.OwnerQuestions.Select(question => question.FactKey));
        Assert.All(model.OwnerQuestions, question => AssertSource(fixture.Root, question.Source));
    }

    [Fact(DisplayName = "XML solutions provide high confidence membership with line provenance")]
    public void XmlSolutionIsDiscovered()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.Delete(Path.Combine(fixture.Root, "Sample.sln"));
        File.WriteAllText(Path.Combine(fixture.Root, "Sample.slnx"), $"<Solution>\n<Project Path=\"{Library}\" />\n<Project Path=\"{Vstest}\" />\n<Project Path=\"{Mtp}\" />\n</Solution>");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(3, model.Units.Count);
        Assert.All(model.Units, unit =>
        {
            Assert.Equal(FactConfidence.High, unit.Location.Confidence);
            Assert.Equal("Sample.slnx", unit.Location.Source.Path);
            AssertSource(fixture.Root, unit.Location.Source);
        });
        Assert.Empty(model.OwnerQuestions);
    }

    [Fact(DisplayName = "Multiple solutions ask the owner to confirm canonical membership")]
    public void MultipleSolutionsLowerMembershipConfidence()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.Copy(Path.Combine(fixture.Root, "Sample.sln"), Path.Combine(fixture.Root, "Other.sln"));
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        Assert.Equal(3, model.Units.Count);
        Assert.All(model.Units, unit => Assert.Equal(FactConfidence.Medium, unit.Location.Confidence));
        Assert.Equal(6, model.OwnerQuestions.Count);
        Assert.All(model.OwnerQuestions, question => Assert.Contains("canonical solution", question.Question));
    }

    [Fact(DisplayName = "Malformed project XML produces uncertainty instead of dropping the unit")]
    public void MalformedProjectIsReported()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, Vstest), "<Project><PropertyGroup>");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);

        var unit = model.Units.Single(unit => unit.Id == Vstest);
        Assert.Equal(FactConfidence.Low, unit.Location.Confidence);
        Assert.Null(unit.IsTest.Value);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"units/{Vstest}/location");
        Assert.Equal(FactConfidence.Low, model.TestSetups.Single(setup => setup.UnitId == Vstest).Runner.Confidence);
    }

    [Fact(DisplayName = "Malformed XML solution still reports projects and a solution question")]
    public void MalformedSolutionIsReported()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        File.WriteAllText(Path.Combine(fixture.Root, "Broken.slnx"), "<Solution>");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(2, model.Units.Count);
        Assert.All(model.Units, unit => Assert.Equal(FactConfidence.Low, unit.Location.Confidence));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "solutions/Broken.slnx");
    }

    [Theory(DisplayName = "Unresolvable references retain low confidence edges and owner questions")]
    [InlineData("$(LibraryPath)/Library.csproj")]
    [InlineData("../../src/*/Library.csproj")]
    [InlineData("../../../../Outside.csproj")]
    [InlineData("Missing.csproj")]
    public void UnresolvableReferenceIsReported(string include)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var path = Path.Combine(fixture.Root, Vstest);
        File.WriteAllText(path, File.ReadAllText(path).Replace("..\\..\\src\\Sample.Library\\Sample.Library.csproj", include, StringComparison.Ordinal));
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var edge = model.Dependencies.Single(edge => edge.FromUnit == Vstest);
        Assert.Equal(FactConfidence.Low, edge.Confidence);
        AssertSource(fixture.Root, edge.Source);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"dependencies/{edge.FromUnit}/{edge.ToUnit}");
    }

    [Fact(DisplayName = "Conditional runner declarations are never treated as unconditional facts")]
    public void ConditionalRunnerRemainsUndetermined()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var path = Path.Combine(fixture.Root, Mtp);
        File.WriteAllText(path, File.ReadAllText(path).Replace("<UseMicrosoftTestingPlatformRunner>",
            "<UseMicrosoftTestingPlatformRunner Condition=\"'$(TargetFramework)' == 'net10.0'\">", StringComparison.Ordinal));
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var runner = model.TestSetups.Single(setup => setup.UnitId == Mtp).Runner;
        Assert.Equal("undetermined", runner.Value);
        Assert.Equal(FactConfidence.Low, runner.Confidence);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"tests/{Mtp}/runner");
    }

    [Fact(DisplayName = "A global runner conflict is referred to the owner with global file provenance")]
    public void GlobalRunnerConflictIsReported()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "global.json"), "{\n  \"test\": {\"runner\": \"Microsoft.Testing.Platform\"}\n}");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var runner = model.TestSetups.Single(setup => setup.UnitId == Vstest).Runner;
        Assert.Equal("undetermined", runner.Value);
        Assert.Equal(FactConfidence.Low, runner.Confidence);
        Assert.Equal(new FactSource("global.json", 2), runner.Source);
        Assert.Equal("MTP", model.TestSetups.Single(setup => setup.UnitId == Mtp).Runner.Value);
    }

    [Fact(DisplayName = "Build outputs are excluded from repository project discovery")]
    public void GeneratedProjectsAreSkipped()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        foreach (var directory in new[] { "bin", "obj", ".git", ".scratch", ".orchestrator-prototype" })
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, directory));
            File.WriteAllText(Path.Combine(fixture.Root, directory, "Noise.csproj"), "<Project />");
        }
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(3, model.Units.Count);
        Assert.Empty(model.OwnerQuestions);
    }

    [Fact(DisplayName = "A missing repository root fails loudly")]
    public void MissingRootIsRejected()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        Assert.Throws<DirectoryNotFoundException>(() => new DotnetProjectDiscoveryAdapter().Discover(Path.Combine(fixture.Root, "missing")));
    }

    private static void AssertSource(string root, FactSource source)
    {
        Assert.NotNull(source.Path);
        Assert.DoesNotContain('\\', source.Path);
        Assert.DoesNotContain(':', source.Path);
        Assert.DoesNotContain("..", source.Path);
        Assert.False(Path.IsPathRooted(source.Path));
        Assert.True(source.Line > 0);
        Assert.True(File.Exists(Path.Combine(root, source.Path)));
        Assert.Null(source.MeasurementReference);
    }
}
