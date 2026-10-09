using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private fixture files; negative controls never compile into the test assembly.
public sealed class DotnetSharedStateHazardUnresolvedNameTests
{
    [Theory]
    [InlineData("public static readonly string Shared = \"Computed\";")]
    [InlineData("public const string Shared = \"Concat\" + \"enated\";")]
    [InlineData("public const string Shared = nameof(HazardNames);")]
    public void NonLiteralName_ProducesLowEmptyKeyAndOneOwnerQuestion(string declaration)
    {
        using var fixture = new ProjectOnboardingFixture("shared-state-hazards");
        File.WriteAllText(Path.Combine(fixture.Root, "tests/Hazard.Tests/Collections.cs"),
            "using Xunit;\n[CollectionDefinition(HazardNames.Shared, DisableParallelization = true)]\npublic class Serial;");
        File.WriteAllText(Path.Combine(fixture.Root, "tests/Hazard.Tests/Support/HazardNames.cs"),
            $"public static class HazardNames {{ {declaration} }}");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var hazard = Assert.Single(model.Hazards);
        Assert.Equal("tests/Hazard.Tests/Hazard.Tests.csproj", hazard.UnitId);
        Assert.Equal("HazardNames.Shared", hazard.Name);
        Assert.Equal("", hazard.IsolationKey.Value);
        Assert.Equal(FactConfidence.Low, hazard.IsolationKey.Confidence);
        Assert.Equal(new FactSource("tests/Hazard.Tests/Collections.cs", 2), hazard.IsolationKey.Source);
        var question = Assert.Single(model.OwnerQuestions.Where(question =>
            question.FactKey.StartsWith("hazards/", StringComparison.Ordinal)));
        Assert.Equal("hazards/tests/Hazard.Tests/Hazard.Tests.csproj/tests/Hazard.Tests/Collections.cs#2", question.FactKey);
        Assert.Equal(hazard.IsolationKey.Source, question.Source);
    }
}
