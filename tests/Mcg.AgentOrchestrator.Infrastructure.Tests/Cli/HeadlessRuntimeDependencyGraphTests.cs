using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

public sealed class HeadlessRuntimeDependencyGraphTests
{
    [Fact]
    public void HeadlessRuntimeExcludesWebFrameworkAndDashboard()
    {
        var root = FindRepositoryRoot();
        var appProject = Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Mcg.AgentOrchestrator.App.csproj");
        var document = XDocument.Load(appProject);
        var sdk = document.Root?.Attribute("Sdk")?.Value;

        Assert.True(
            string.Equals("Microsoft.NET.Sdk", sdk, StringComparison.Ordinal),
            $"Expected headless App SDK, actual '{sdk}'.");

        var references = document.Descendants()
            .Where(element => element.Name.LocalName is "FrameworkReference" or "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(references, reference =>
            reference.Contains("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, reference =>
            reference.Contains("Mcg.AgentOrchestrator.Dashboard", StringComparison.OrdinalIgnoreCase));

        var appAssemblyInfo = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(appProject)!,
            "Properties",
            "AssemblyInfo.cs"));
        Assert.DoesNotContain(
            "InternalsVisibleTo(\"Mcg.AgentOrchestrator.Dashboard\")",
            appAssemblyInfo,
            StringComparison.Ordinal);

        var dashboardApiTypes = typeof(CliArgumentParser).Assembly.GetTypes()
            .Where(type => type.Namespace?.Equals(
                "Mcg.AgentOrchestrator.App.Dashboard.Api",
                StringComparison.Ordinal) == true)
            .Select(type => type.FullName)
            .ToArray();
        Assert.Empty(dashboardApiTypes);

        var appDirectory = Path.GetDirectoryName(appProject)!;
        var aspNetSources = Directory.EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment is "bin" or "obj"))
            .Where(path => File.ReadAllText(path).Contains("Microsoft.AspNetCore", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();
        Assert.All(aspNetSources, path => Assert.True(
            path.Contains("Dashboard", StringComparison.OrdinalIgnoreCase) &&
            (path.Contains("Hosting", StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(path).StartsWith("DashboardEndpoints", StringComparison.Ordinal) ||
             Path.GetFileName(path) is "DashboardEndpointServices.cs" or "DashboardRequestParser.Routing.cs"),
            $"ASP.NET source is not owned by a dashboard Compile Remove/Include seam: {path}"));

        var dashboardProject = Path.Combine(
            root, "src", "Mcg.AgentOrchestrator.Dashboard", "Mcg.AgentOrchestrator.Dashboard.csproj");
        var dashboard = XDocument.Load(dashboardProject);
        Assert.Equal("Microsoft.NET.Sdk.Web", dashboard.Root?.Attribute("Sdk")?.Value);
        Assert.Contains(dashboard.Descendants(), element =>
            element.Name.LocalName == "ProjectReference" &&
            element.Attribute("Include")?.Value.Contains("Mcg.AgentOrchestrator.App", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void HeadlessPublishPreservesInspectableDependencyInventory()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "publish-headless.ps1"));

        Assert.DoesNotContain("-p:PublishSingleFile=true", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-p:PublishSingleFile=false", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Headless publish did not produce", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
