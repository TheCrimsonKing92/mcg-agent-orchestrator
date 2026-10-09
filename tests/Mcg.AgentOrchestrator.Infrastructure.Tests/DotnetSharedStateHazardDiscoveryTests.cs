using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test owns its fixture's files and does not compile or execute them.
public sealed class DotnetSharedStateHazardDiscoveryTests
{
    [Fact]
    public void SerialDefinitions_LiteralAndSameUnitConstant_LearnTwoHighKeys()
    {
        using var fixture = new ProjectOnboardingFixture("shared-state-hazards");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(2, model.Hazards.Count);
        Assert.Equal(new[] { "xunit:LiteralSerial", "xunit:SharedDatabase" },
            model.Hazards.Select(hazard => hazard.IsolationKey.Value));
        Assert.Equal(new[] { "LiteralSerial", "SharedDatabase" }, model.Hazards.Select(hazard => hazard.Name));
        Assert.All(model.Hazards, hazard =>
        {
            Assert.Equal("tests/Hazard.Tests/Hazard.Tests.csproj", hazard.UnitId);
            Assert.Equal(FactConfidence.High, hazard.IsolationKey.Confidence);
            Assert.Equal("tests/Hazard.Tests/Collections.cs", hazard.IsolationKey.Source.Path);
        });
        Assert.Equal(new[] { 2, 4 }, model.Hazards.Select(hazard => hazard.IsolationKey.Source.Line!.Value));
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.StartsWith("hazards/", StringComparison.Ordinal));
    }

    [Fact]
    public void AssemblyBehavior_ExplicitDisabledParallelization_LearnsUnitWideKey()
    {
        using var fixture = new ProjectOnboardingFixture("shared-state-hazards");
        File.WriteAllText(Path.Combine(fixture.Root, "tests/Hazard.Tests/AssemblyInfo.cs"),
            "using Xunit;\n[assembly: CollectionBehavior(DisableTestParallelization = true)]\n");
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(3, model.Hazards.Count);
        var hazard = Assert.Single(model.Hazards.Where(hazard => hazard.IsolationKey.Value == "xunit:*"));
        Assert.Equal("assembly:DisableTestParallelization", hazard.Name);
        Assert.Equal("tests/Hazard.Tests/Hazard.Tests.csproj", hazard.UnitId);
        Assert.Equal(FactConfidence.High, hazard.IsolationKey.Confidence);
        Assert.Equal(new FactSource("tests/Hazard.Tests/AssemblyInfo.cs", 2), hazard.IsolationKey.Source);
    }

    [Fact]
    public void DuplicateAndNonHazardSyntax_OnlyFirstSerialDeclarationSurvives()
    {
        using var fixture = new ProjectOnboardingFixture("shared-state-hazards");
        File.WriteAllText(Path.Combine(fixture.Root, "tests/Hazard.Tests/ZOther.cs"), """
            [Xunit.CollectionDefinitionAttribute("LiteralSerial", DisableParallelization = true)]
            public sealed class DuplicateCollection;
            [Xunit.Collection("UsageOnly")]
            public sealed class Usage;
            [assembly: Xunit.CollectionBehavior(DisableTestParallelization = false)]
            [assembly: Xunit.CollectionBehavior(MaxParallelThreads = 1)]
            // [Xunit.CollectionDefinition("CommentOnly", DisableParallelization = true)]
            public class Strings { const string Text = "[CollectionDefinition(\"StringOnly\", DisableParallelization = true)]"; }
            """);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Equal(2, model.Hazards.Count);
        var literal = Assert.Single(model.Hazards.Where(hazard => hazard.Name == "LiteralSerial"));
        Assert.Equal(new FactSource("tests/Hazard.Tests/Collections.cs", 2), literal.IsolationKey.Source);
    }

    [Fact]
    public void NestedProjectAndBuildOutputs_DoNotBelongToParentUnit()
    {
        using var fixture = new ProjectOnboardingFixture("shared-state-hazards");
        var directory = Path.Combine(fixture.Root, "tests/Hazard.Tests");
        foreach (var child in new[] { "bin", "obj", "Nested" })
        {
            Directory.CreateDirectory(Path.Combine(directory, child));
            File.WriteAllText(Path.Combine(directory, child, "Collections.cs"),
                $"[Xunit.CollectionDefinition(\"{child}\", DisableParallelization = true)] public class Serial;");
        }
        File.WriteAllText(Path.Combine(directory, "Nested/Nested.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>
            """);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var parent = model.Hazards.Where(hazard => hazard.UnitId == "tests/Hazard.Tests/Hazard.Tests.csproj").ToArray();
        Assert.Equal(2, parent.Length);
        var nested = Assert.Single(model.Hazards.Where(hazard => hazard.UnitId.EndsWith("Nested.csproj", StringComparison.Ordinal)));
        Assert.Equal("xunit:Nested", nested.IsolationKey.Value);
        Assert.DoesNotContain(model.Hazards, hazard => hazard.IsolationKey.Value is "xunit:bin" or "xunit:obj");
    }
}
