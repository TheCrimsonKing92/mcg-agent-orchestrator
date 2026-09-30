using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

// Parallel-safe: assembly reflection and read-only project inspection.
public sealed class AppAssemblyDashboardIndependenceTests
{
    [Fact]
    public void AppAssemblyOwnsCliControlsAndContainsNoDashboardTypes()
    {
        var assembly = typeof(CliArgumentParser).Assembly;
        Assert.DoesNotContain(assembly.GetTypes(), type =>
            type.Namespace?.StartsWith("Mcg.AgentOrchestrator.App.Dashboard", StringComparison.Ordinal) == true);
        Assert.NotNull(assembly.GetType("Mcg.AgentOrchestrator.App.Cli.NextActionControls"));
    }

    [Fact]
    public void AppProjectDoesNotIncludeDashboardSource()
    {
        var project = Path.Combine(FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App",
            "Mcg.AgentOrchestrator.App.csproj");
        Assert.True(File.Exists(project), $"Missing App project: {project}");
        var compileElements = XDocument.Load(project).Descendants()
            .Where(element => element.Name.LocalName == "Compile").ToArray();

        Assert.Contains(compileElements, element => element.Attribute("Remove") is not null);
        Assert.DoesNotContain(compileElements, element =>
            element.Attribute("Include")?.Value.Replace('\\', '/').StartsWith(
                "Dashboard/", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (CliVerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
