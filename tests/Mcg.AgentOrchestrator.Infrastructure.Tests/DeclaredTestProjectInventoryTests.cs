using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DeclaredTestProjectInventoryTests
{
    private const string CliProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/" +
        "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
    private const string CliProjectWindowsSeparators =
        "tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\Cli\\" +
        "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
    private const string ProbeProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/RealProcessShardProbe/" +
        "Mcg.AgentOrchestrator.RealProcessShardProbe.csproj";

    private static AcceptanceGateEngineSettings SettingsFor(params string[] projects) =>
        new()
        {
            MtpInvocations = projects
                .Select(project => new AcceptanceMtpInvocation { Project = project })
                .ToArray()
        };

    [Xunit.Theory]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj")]
    [Xunit.InlineData(CliProject)]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Cli.Tests/Mcg.AgentOrchestrator.Cli.Tests.csproj")]
    [Xunit.InlineData(CliProjectWindowsSeparators)]
    public void DeclaredShapeAcceptsCandidateRelativeTestProjectsAnywhereUnderTests(string project) =>
        Assert.True(DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(project));

    [Xunit.Theory]
    [Xunit.InlineData((string?)null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    [Xunit.InlineData(ProbeProject)]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.TestSupport/Mcg.AgentOrchestrator.TestSupport.csproj")]
    [Xunit.InlineData("tests/../../evil/tests/Mcg.AgentOrchestrator.Evil.Tests.csproj")]
    [Xunit.InlineData("tests/./Mcg.AgentOrchestrator.Evil.Tests.csproj")]
    [Xunit.InlineData("C:/attacker/tests/Mcg.AgentOrchestrator.Evil.Tests.csproj")]
    [Xunit.InlineData("//server/share/tests/Mcg.AgentOrchestrator.Evil.Tests.csproj")]
    [Xunit.InlineData("tests//Mcg.AgentOrchestrator.Evil.Tests.csproj")]
    public void DeclaredShapeRejectsNonTestRootedAndTraversalProjects(string? project) =>
        Assert.False(DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(project));

    [Xunit.Fact]
    public void DeclaredProjectsSkipsIneligibleManifestEntriesAndDeduplicates()
    {
        var declared = DeclaredTestProjectInventory.DeclaredProjects(SettingsFor(
            CliProject,
            ProbeProject,
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            CliProjectWindowsSeparators));

        Assert.Equal(new[] { CliProject }, declared);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Infrastructure.Cli.Tests")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.Infrastructure.Cli.Tests")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj")]
    [Xunit.InlineData(CliProject)]
    [Xunit.InlineData(CliProjectWindowsSeparators)]
    public void TryResolveAcceptsEveryDeclaredFormAndReturnsTheCandidateRelativeProject(string alias)
    {
        Assert.True(DeclaredTestProjectInventory.TryResolve(alias, SettingsFor(CliProject), out var project));
        Assert.Equal(CliProject, project);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Unsupported.Tests")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.App")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.RealProcessShardProbe")]
    [Xunit.InlineData(ProbeProject)]
    [Xunit.InlineData("../../../evil/tests/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj")]
    [Xunit.InlineData("C:/attacker/tests/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj")]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    public void TryResolveRefusesUndeclaredTraversalAndAbsoluteAliases(string alias)
    {
        Assert.False(DeclaredTestProjectInventory.TryResolve(
            alias,
            SettingsFor(CliProject, ProbeProject),
            out var project));
        Assert.Equal(string.Empty, project);
    }

    [Xunit.Fact]
    public void TryResolveWithoutSettingsResolvesNothing()
    {
        Assert.False(DeclaredTestProjectInventory.TryResolve(
            "Infrastructure.Cli.Tests",
            engineSettings: null,
            out var project));
        Assert.Equal(string.Empty, project);
    }

    [Xunit.Fact]
    public void DescribeSupportedProjectFormsKeepsLegacyAliasesAndNamesDeclaredLabels()
    {
        var described = DeclaredTestProjectInventory.DescribeSupportedProjectForms(
            SettingsFor(CliProject, ProbeProject));

        Assert.StartsWith("Core, Core.Tests", described, StringComparison.Ordinal);
        Assert.Contains("(declared: Infrastructure.Cli.Tests)", described, StringComparison.Ordinal);
        Assert.DoesNotContain("RealProcessShardProbe", described, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DescribeSupportedProjectFormsWithoutDeclaredProjectsIsLegacyFormsOnly() =>
        Assert.Equal(
            DeclaredTestProjectInventory.LegacyAliasProjectForms,
            DeclaredTestProjectInventory.DescribeSupportedProjectForms(SettingsFor(ProbeProject)));
}
