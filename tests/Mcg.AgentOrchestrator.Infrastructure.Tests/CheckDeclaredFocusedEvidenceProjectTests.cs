using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CheckDeclaredFocusedEvidenceProjectTests
{
    internal const string CoreProject = "tests/NetHealth.Core.Tests/NetHealth.Core.Tests.csproj";
    internal const string AppProject = "tests/NetHealth.App.Tests/NetHealth.App.Tests.csproj";
    internal const string Manifest = """
        {
          "checks": [
            {
              "name": "net-health core checks", "type": "dotnet-test", "runner": "vstest",
              "project": "tests/NetHealth.Core.Tests/NetHealth.Core.Tests.csproj",
              "arguments": ["--verbosity", "quiet"]
            },
            {
              "name": "net-health app checks", "type": "dotnet-test", "runner": "vstest",
              "project": "tests/NetHealth.App.Tests/NetHealth.App.Tests.csproj",
              "arguments": ["--configuration", "Release", "--filter", "Category!=Slow"],
              "timeoutMinutes": 17
            }
          ]
        }
        """;

    [Theory]
    [InlineData(CoreProject, CoreProject)]
    [InlineData("tests\\NetHealth.Core.Tests\\NetHealth.Core.Tests.csproj", CoreProject)]
    [InlineData("NetHealth.Core.Tests.csproj", CoreProject)]
    [InlineData("NetHealth.Core.Tests", CoreProject)]
    [InlineData("net-health core checks", CoreProject)]
    [InlineData(AppProject, AppProject)]
    [InlineData("tests\\NetHealth.App.Tests\\NetHealth.App.Tests.csproj", AppProject)]
    [InlineData("NetHealth.App.Tests.csproj", AppProject)]
    [InlineData("NetHealth.App.Tests", AppProject)]
    [InlineData("net-health app checks", AppProject)]
    public void Resolve_CheckDeclaredAlias_ReturnsDeclaredPath(string alias, string expected)
    {
        var settings = AcceptanceGateEngineSettings.Parse(Manifest);

        Assert.Empty(settings.MtpInvocations);
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(alias, settings, out var project));
        Assert.Equal(expected, project);
    }

    [Theory]
    [InlineData("Undeclared.Tests")]
    [InlineData("tests/../../evil/tests/NetHealth.Core.Tests/NetHealth.Core.Tests.csproj")]
    [InlineData("C:/attacker/tests/NetHealth.Core.Tests/NetHealth.Core.Tests.csproj")]
    [InlineData("//server/share/tests/NetHealth.Core.Tests/NetHealth.Core.Tests.csproj")]
    public void Resolve_UndeclaredOrUnsafeAlias_Refuses(string alias)
    {
        Assert.False(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
            alias, AcceptanceGateEngineSettings.Parse(Manifest), out var project));
        Assert.Equal(string.Empty, project);
    }

    [Fact]
    public void Describe_CheckOnlyManifest_ListsProjectLabels()
    {
        var forms = GoalAcceptanceVerifier.FocusedEvidenceSupportedProjectForms(
            AcceptanceGateEngineSettings.Parse(Manifest));

        Assert.StartsWith(DeclaredTestProjectInventory.LegacyAliasProjectForms, forms, StringComparison.Ordinal);
        Assert.Contains("(declared: NetHealth.App.Tests, NetHealth.Core.Tests)", forms, StringComparison.Ordinal);
        Assert.DoesNotContain("engine.mtpInvocations", forms, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_ExplicitChecks_AppendsEligibleProjectsAfterMtp()
    {
        var settings = AcceptanceGateEngineSettings.Parse("""
            {
              "engine": { "mtpInvocations": [
                { "project": "tests/NetHealth.Core.Tests/NetHealth.Core.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}{executableExtension}",
                  "arguments": ["{executable}"] }
              ] },
              "checks": [
                { "type": "dotnet-test", "project": "tests\\NETHEALTH.CORE.TESTS\\NETHEALTH.CORE.TESTS.CSPROJ" },
                { "type": "dotnet-test", "project": "tests/NetHealth.App.Tests/NetHealth.App.Tests.csproj" },
                { "type": "dotnet-test", "project": "tests/NetHealth.App.Tests/NetHealth.App.Tests.csproj" },
                { "type": "dotnet-test", "project": "tests/Other.Tests/Other.Tests.csproj" },
                { "type": "command", "project": "tests/Command.Tests/Command.Tests.csproj" },
                { "type": "dotnet-test", "project": "src/Invalid.Tests/Invalid.Tests.csproj" },
                { "type": "dotnet-test", "project": "tests/../../Invalid.Tests/Invalid.Tests.csproj" },
                { "type": "dotnet-test", "project": "C:/tests/Invalid.Tests/Invalid.Tests.csproj" },
                { "type": "dotnet-test", "project": "tests/Probe/Probe.csproj" }
              ]
            }
            """);

        Assert.Equal([CoreProject], DeclaredTestProjectInventory.DeclaredProjects(settings));
        Assert.Equal([CoreProject, AppProject, "tests/Other.Tests/Other.Tests.csproj"],
            DeclaredTestProjectInventory.DeclaredProjects(settings, settings.DeclaredDotnetTestChecks));
        Assert.False(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("Command.Tests", settings, out _));
        Assert.False(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("Invalid.Tests", settings, out _));
        Assert.False(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("Probe", settings, out _));
    }

    [Theory]
    [InlineData("vstest")]
    [InlineData("mtp")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void Inventory_DotnetTestCheck_DeclaresProjectRegardlessOfRunner(string? runner)
    {
        var settings = AcceptanceGateEngineSettings.Parse(JsonSerializer.Serialize(new
        {
            engine = new { },
            checks = new[] { new { type = "DOTNET-TEST", project = AppProject, runner } }
        }));

        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("NetHealth.App.Tests", settings, out var project));
        Assert.Equal(AppProject, project);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"checks\":null}")]
    [InlineData("{\"checks\":{}}")]
    [InlineData("{\"checks\":[null,42,{\"type\":\"dotnet-test\",\"project\":42}]}")]
    public void Parse_UnreadableChecks_KeepsLegacyResolution(string manifest)
    {
        var settings = AcceptanceGateEngineSettings.Parse(manifest);

        Assert.Empty(settings.DeclaredDotnetTestChecks);
        Assert.False(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("NetHealth.App.Tests", settings, out _));
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("Core.Tests", settings, out var project));
        Assert.Equal(AcceptancePolicyShardPlanner.CoreTestsProject, project);
    }

    [Theory]
    [InlineData("../unsafe")]
    [InlineData("C:/unsafe")]
    public void Resolve_PathBearingCheckName_DoesNotBypassPathEligibility(string name)
    {
        var settings = AcceptanceGateEngineSettings.Parse(JsonSerializer.Serialize(new
        {
            checks = new[] { new { type = "dotnet-test", project = AppProject, name } }
        }));

        Assert.False(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(name, settings, out var project));
        Assert.Equal(string.Empty, project);
    }
}
