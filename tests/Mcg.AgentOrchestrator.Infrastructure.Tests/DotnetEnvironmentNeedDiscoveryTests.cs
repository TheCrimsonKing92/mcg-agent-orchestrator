using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all declarations come from this test's isolated fixture copy.
public sealed class DotnetEnvironmentNeedDiscoveryTests
{
    [Fact]
    public void SdkAndEveryManifestToolHavePinnedValuesAndSources()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        string[] expectedNames = ["dotnet-sdk", "dotnet-ef", "sample-tool"];
        string[] expectedVersions = ["10.0.100", "10.0.0", "1.2.3"];
        Assert.Equal(expectedNames, model.EnvironmentNeeds.Select(need => need.Name));
        Assert.Equal(expectedVersions, model.EnvironmentNeeds.Select(need => need.Value.Value));
        var sdk = model.EnvironmentNeeds[0].Value;
        Assert.Equal(new FactSource("global.json", 3), sdk.Source);
        Assert.Contains("\"version\"", Line(fixture.Root, sdk.Source));
        foreach (var tool in model.EnvironmentNeeds.Skip(1))
        {
            Assert.Equal(".config/dotnet-tools.json", tool.Value.Source.Path);
            Assert.Contains($"\"{tool.Name}\"", Line(fixture.Root, tool.Value.Source));
        }
        Assert.All(model.EnvironmentNeeds, need => Assert.Equal(FactConfidence.High, need.Value.Confidence));
        Assert.Empty(model.OwnerQuestions);
    }

    [Fact]
    public void MissingPinsNeverInventVersionsOrTools()
    {
        using var fixture = new ProjectOnboardingFixture("no-solution");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var need = Assert.Single(model.EnvironmentNeeds);
        Assert.Equal("dotnet-sdk", need.Name);
        Assert.Equal("undetermined", need.Value.Value);
        Assert.Equal(FactConfidence.Low, need.Value.Confidence);
        Assert.Contains("Sdk=", Line(fixture.Root, need.Value.Source));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "environment/dotnet-sdk");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sdk\":{\"version\":false}}")]
    [InlineData("{ malformed")]
    public void InvalidSdkPinProducesQuestion(string json)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "global.json"), json);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var need = model.EnvironmentNeeds.Single(need => need.Name == "dotnet-sdk");
        Assert.Equal("undetermined", need.Value.Value);
        Assert.Equal(FactConfidence.Low, need.Value.Confidence);
        Assert.Equal(new FactSource("global.json", 1), need.Value.Source);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "environment/dotnet-sdk");
    }

    [Fact]
    public void RootManifestFallbackAndMissingToolVersionAreReported()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.Delete(Path.Combine(fixture.Root, ".config", "dotnet-tools.json"));
        File.WriteAllText(Path.Combine(fixture.Root, "dotnet-tools.json"), "{\"tools\":{\"unversioned\":{}}}");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var tool = model.EnvironmentNeeds.Single(need => need.Name == "unversioned");
        Assert.Equal("undetermined", tool.Value.Value);
        Assert.Equal(FactConfidence.Low, tool.Value.Confidence);
        Assert.Equal(new FactSource("dotnet-tools.json", 1), tool.Value.Source);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "environment/unversioned");
    }

    [Fact]
    public void MalformedManifestProducesQuestionWithoutInventingTools()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, ".config", "dotnet-tools.json"), "{ malformed");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal("dotnet-sdk", Assert.Single(model.EnvironmentNeeds).Name);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "environment/tool-manifest");
    }

    [Fact]
    public void VersionSourceIgnoresCommentsAndOtherObjects()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        File.WriteAllText(Path.Combine(fixture.Root, "global.json"), "{\n// \"version\" is a comment\n\"other\":{\"version\":\"wrong\"},\n\"sdk\":{\n\"version\":\"10.0.100\"\n}\n}");
        var sdk = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root).EnvironmentNeeds[0].Value;
        Assert.Equal("10.0.100", sdk.Value);
        Assert.Equal(new FactSource("global.json", 5), sdk.Source);
    }

    [Fact]
    public void Utf8PreamblesDoNotHideSdkOrToolPins()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        foreach (var relative in new[] { "global.json", ".config/dotnet-tools.json" })
        {
            var path = Path.Combine(fixture.Root, relative);
            File.WriteAllText(path, File.ReadAllText(path), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(3, model.EnvironmentNeeds.Count);
        Assert.All(model.EnvironmentNeeds, need => Assert.Equal(FactConfidence.High, need.Value.Confidence));
        Assert.Empty(model.OwnerQuestions);
    }

    private static string Line(string root, FactSource source) =>
        File.ReadAllLines(Path.Combine(root, source.Path!))[source.Line!.Value - 1];
}
