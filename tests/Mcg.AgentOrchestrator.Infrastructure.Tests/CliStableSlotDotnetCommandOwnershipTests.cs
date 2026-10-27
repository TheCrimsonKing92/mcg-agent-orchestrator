using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliStableSlotDotnetCommandOwnershipTests
{
    private const BindingFlags DeclaredStatic = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const string OwnerSourcePath = "src/Mcg.AgentOrchestrator.App/Cli/CliStableSlotDotnetCommand.cs";

    [Fact]
    public void StableSlotCommand_HasOneOwnerAndOnlyCompatibilityForwardersRemain()
    {
        var handlers = typeof(CliCommandHandlers);
        string[] removed = ["RunStableSlotDotnet", "RunStableSlotMtpTest", "ReadStableSlotOption"];
        foreach (var name in removed)
        {
            Assert.Empty(handlers.GetMember(name, DeclaredStatic));
        }

        string[] forwarders =
        [
            "ShouldBuildStableSlotMtpProject", "ResolveStableSlotResultsDirectory",
            "TranslateStableSlotMtpFilter", "RunStableSlotProcess"
        ];
        foreach (var name in forwarders)
        {
            var forwarder = Assert.IsAssignableFrom<MethodInfo>(Assert.Single(handlers.GetMember(name, DeclaredStatic)));
            Assert.True(forwarder.IsAssembly, name);
            Assert.True(forwarder.IsStatic, name);
        }

        var owner = typeof(CliStableSlotDotnetCommand);
        Assert.True(owner.IsAbstract);
        Assert.True(owner.IsSealed);
        Assert.False(owner.IsNested);
        Assert.True(owner.IsNotPublic);
        Assert.Equal("Mcg.AgentOrchestrator.App.Cli", owner.Namespace);
        string[] internalMethods = ["Run", "ShouldBuildMtpProject", "ResolveResultsDirectory", "TranslateMtpFilter", "RunProcess"];
        string[] privateMethods = ["RunMtpTest", "ReadOption"];
        foreach (var name in internalMethods.Concat(privateMethods))
        {
            var method = Assert.IsAssignableFrom<MethodInfo>(Assert.Single(owner.GetMember(name, DeclaredStatic)));
            Assert.True(method.IsStatic, name);
            if (internalMethods.Contains(name))
            {
                Assert.True(method.IsAssembly, name);
            }
            else
            {
                Assert.True(method.IsPrivate, name);
            }
        }
    }

    [Fact]
    public void FilterForwarder_PreservesSequence()
    {
        const string filter = "FullyQualifiedName~Alpha|fullyqualifiedname !~ Beta.Gamma&Delta";

        Assert.Equal(CliStableSlotDotnetCommand.TranslateMtpFilter(filter),
            CliCommandHandlers.TranslateStableSlotMtpFilter(filter));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BuildDecisionForwarder_PreservesDecision(bool noBuild, bool artifactsExist)
    {
        Assert.Equal(CliStableSlotDotnetCommand.ShouldBuildMtpProject(noBuild, artifactsExist),
            CliCommandHandlers.ShouldBuildStableSlotMtpProject(noBuild, artifactsExist));
    }

    [Fact]
    public void ResultsDirectoryForwarder_PreservesDefaultRelativeAndRootedPaths()
    {
        var workspace = Path.GetFullPath("stable-slot-workspace");
        var defaultResults = Path.Combine(workspace, "TestResults");
        string?[] configuredPaths = [null, "focused", Path.Combine(workspace, "elsewhere")];
        foreach (var configured in configuredPaths)
        {
            Assert.Equal(CliStableSlotDotnetCommand.ResolveResultsDirectory(configured, workspace, defaultResults),
                CliCommandHandlers.ResolveStableSlotResultsDirectory(configured, workspace, defaultResults));
        }
    }

    [Fact]
    public void SourceLiteralsAndProcessLaunch_BelongOnlyToTheCommand()
    {
        var root = VerifiedRepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepositorySourceInventory.IsExcludedRelativePath(Path.GetRelativePath(root, path)))
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'), Text: File.ReadAllText(path)))
            .ToArray();
        string[] literals =
        [
            "Usage: stable-slot-dotnet <dotnet-arguments>", "Unsupported stable-slot MTP filter",
            "MTP test executable was not produced", "cli-process-stdout-drain", "Failed to start dotnet process."
        ];
        foreach (var literal in literals)
        {
            var source = Assert.Single(sources.Where(source => source.Text.Contains(literal, StringComparison.Ordinal)));
            Assert.Equal(OwnerSourcePath, source.Path);
        }

        var ownerSource = Assert.Single(sources.Where(source => source.Path == OwnerSourcePath)).Text;
        Assert.DoesNotContain("partial class", ownerSource);
        var handlerSource = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.System.cs"));
        Assert.Equal(1, Regex.Matches(handlerSource, Regex.Escape("CliStableSlotDotnetCommand.Run(")).Count);
        Assert.DoesNotContain("Process.Start(", handlerSource);
        Assert.DoesNotContain("new ProcessStartInfo", handlerSource);
    }
}
