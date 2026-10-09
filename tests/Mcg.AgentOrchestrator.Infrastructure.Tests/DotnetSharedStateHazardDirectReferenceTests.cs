using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: source-only fixture repositories are generated in private temporary directories.
public sealed class DotnetSharedStateHazardDirectReferenceTests
{
    private const string Unit = "tests/Hazard.Tests/Hazard.Tests.csproj";
    private static readonly FactSource DeclarationSource = new("tests/Hazard.Tests/Collections.cs", 2);

    [Theory]
    [InlineData("SharedNames.Serial")]
    [InlineData("Support.SharedNames.Serial")]
    [InlineData("global::Support.SharedNames.Serial")]
    public void DirectReferenceLiteral_LearnsHighKeyAtCollectionDeclaration(string expression)
    {
        using var fixture = CreateFixture(expression, "SupportA");
        AddSupport(fixture.Root, "SupportA", "public const string Serial = \"SharedDatabase\";");
        // Generated sources must not introduce ambiguity into the referenced unit.
        foreach (var output in new[] { "bin", "obj" })
        {
            var directory = Path.Combine(fixture.Root, "src/SupportA", output);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Names.cs"),
                "namespace Support; public static class SharedNames { public const string Serial = \"GeneratedNoise\"; }");
        }

        AssertResolved(fixture, "SharedDatabase");
    }

    [Theory]
    [InlineData("SameValue", true)]
    [InlineData("DifferentValue", false)]
    public void MultipleDirectReferences_RequireAgreement(string secondValue, bool resolves)
    {
        using var fixture = CreateFixture("SharedNames.Serial", "SupportA", "SupportB");
        AddSupport(fixture.Root, "SupportA", "public const string Serial = \"SameValue\";");
        AddSupport(fixture.Root, "SupportB", $"public const string Serial = \"{secondValue}\";");

        if (resolves)
            AssertResolved(fixture, "SameValue");
        else
            AssertUnresolved(fixture, "SharedNames.Serial");
    }

    [Fact]
    public void TransitiveReferenceLiteral_RemainsUnknown()
    {
        using var fixture = CreateFixture("SharedNames.Serial", "SupportA");
        AddSupport(fixture.Root, "SupportA", "", "SupportB");
        AddSupport(fixture.Root, "SupportB", "public const string Serial = \"TransitiveOnly\";");

        AssertUnresolved(fixture, "SharedNames.Serial");
    }

    [Fact]
    public void BareMemberName_DoesNotResolveFromDirectReference()
    {
        using var fixture = CreateFixture("Serial", "SupportA");
        AddSupport(fixture.Root, "SupportA", "public const string Serial = \"WrongScope\";");

        AssertUnresolved(fixture, "Serial");
    }

    [Fact]
    public void NonLiteralDirectReference_RemainsUnknown()
    {
        using var fixture = CreateFixture("SharedNames.Serial", "SupportA");
        AddSupport(fixture.Root, "SupportA", "public const string Serial = \"Part\" + \"Two\";");

        AssertUnresolved(fixture, "SharedNames.Serial");
    }

    [Fact]
    public void SameUnitLiteral_TakesPrecedenceOverDirectReference()
    {
        using var fixture = CreateFixture("SharedNames.Serial", "SupportA");
        AddSupport(fixture.Root, "SupportA", "public const string Serial = \"External\";");
        File.WriteAllText(Path.Combine(fixture.Root, "tests/Hazard.Tests/Local.cs"),
            "public static class SharedNames { public const string Serial = \"Local\"; }");

        AssertResolved(fixture, "Local");
    }

    [Fact]
    public void NonLiteralSameUnitConstant_CannotBeReplacedByReferencedLiteral()
    {
        using var fixture = CreateFixture("SharedNames.Serial", "SupportA");
        AddSupport(fixture.Root, "SupportA", "public const string Serial = \"External\";");
        File.WriteAllText(Path.Combine(fixture.Root, "tests/Hazard.Tests/Local.cs"),
            "public static class SharedNames { public const string Serial = \"Part\" + \"Two\"; }");

        AssertUnresolved(fixture, "SharedNames.Serial");
    }

    private static ProjectOnboardingFixture CreateFixture(string expression, params string[] references)
    {
        var fixture = new ProjectOnboardingFixture("shared-state-hazards");
        File.WriteAllText(Path.Combine(fixture.Root, "tests/Hazard.Tests/Collections.cs"),
            $"using Xunit;\n[CollectionDefinition({expression}, DisableParallelization = true)]\npublic class Serial;");
        var path = Path.Combine(fixture.Root, Unit);
        var project = XDocument.Load(path);
        project.Root!.Add(new XElement("ItemGroup", references.Select(name =>
            new XElement("ProjectReference", new XAttribute("Include", $"../../src/{name}/{name}.csproj")))));
        project.Save(path);
        return fixture;
    }

    private static void AddSupport(string root, string name, string declaration, params string[] references)
    {
        var directory = Path.Combine(root, "src", name);
        Directory.CreateDirectory(directory);
        var project = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0")),
            new XElement("ItemGroup", references.Select(target =>
                new XElement("ProjectReference", new XAttribute("Include", $"../{target}/{target}.csproj"))))));
        project.Save(Path.Combine(directory, name + ".csproj"));
        File.WriteAllText(Path.Combine(directory, "Names.cs"),
            $"namespace Support; public static class SharedNames {{ {declaration} }}");
    }

    private static void AssertResolved(ProjectOnboardingFixture fixture, string name)
    {
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var hazard = Assert.Single(model.Hazards);
        Assert.Equal(Unit, hazard.UnitId);
        Assert.Equal(name, hazard.Name);
        Assert.Equal("xunit:" + name, hazard.IsolationKey.Value);
        Assert.Equal(FactConfidence.High, hazard.IsolationKey.Confidence);
        Assert.Equal(DeclarationSource, hazard.IsolationKey.Source);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.StartsWith("hazards/", StringComparison.Ordinal));
    }

    private static void AssertUnresolved(ProjectOnboardingFixture fixture, string expression)
    {
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        var hazard = Assert.Single(model.Hazards);
        Assert.Equal(Unit, hazard.UnitId);
        Assert.Equal(expression, hazard.Name);
        Assert.Equal("", hazard.IsolationKey.Value);
        Assert.Equal(FactConfidence.Low, hazard.IsolationKey.Confidence);
        Assert.Equal(DeclarationSource, hazard.IsolationKey.Source);
        var question = Assert.Single(model.OwnerQuestions.Where(question => question.FactKey.StartsWith("hazards/", StringComparison.Ordinal)));
        Assert.Equal($"hazards/{Unit}/{DeclarationSource.Path}#2", question.FactKey);
        Assert.Equal(DeclarationSource, question.Source);
    }
}
