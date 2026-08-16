using System.Xml.Linq;

public sealed class InfrastructureProductionProjectGraphTests
{
    private const string ProvidersProject =
        "src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj";
    private const string CoreProject =
        "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
    private const string InfrastructureProject =
        "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj";
    private const string AppProject =
        "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj";

    [Xunit.Fact(DisplayName = "Infrastructure production project graph is acyclic and Providers owns its seam once")]
    public void ProvidersAssemblyOwnsProviderSources()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var projects = LoadProductionProjects(root);
        var providers = projects[ProvidersProject];
        var providerSources = Directory
            .EnumerateFiles(
                Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure.Providers"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Select(path => Normalize(Path.GetRelativePath(root, path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal([CoreProject], providers.References);
        Assert.DoesNotContain(
            providers.References,
            reference => reference.Equals(
                InfrastructureProject,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ProvidersProject, projects[InfrastructureProject].References);
        Assert.Contains(ProvidersProject, projects[AppProject].References);
        AssertAcyclic(projects);
        Assert.NotEmpty(providerSources);

        foreach (var source in providerSources)
        {
            var owners = projects.Values
                .Where(project => project.OwnsSource(root, source))
                .Select(project => project.Path)
                .ToArray();
            Assert.Equal([ProvidersProject], owners);
        }
    }

    private static Dictionary<string, ProductionProject> LoadProductionProjects(string root)
    {
        var src = Path.Combine(root, "src");
        var projectFiles = Directory
            .EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .ToArray();
        var projectPaths = projectFiles.ToDictionary(
            path => Normalize(Path.GetFullPath(path)),
            path => Normalize(Path.GetRelativePath(root, path)),
            StringComparer.OrdinalIgnoreCase);

        return projectFiles
            .Select(projectFile =>
            {
                var document = XDocument.Load(projectFile, LoadOptions.None);
                var references = document
                    .Descendants()
                    .Where(element => element.Name.LocalName == "ProjectReference")
                    .Select(element => element.Attribute("Include")?.Value)
                    .Where(include => !string.IsNullOrWhiteSpace(include))
                    .Select(include => Normalize(Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(projectFile)!,
                        include!))))
                    .Where(projectPaths.ContainsKey)
                    .Select(reference => projectPaths[reference])
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var explicitCompileFiles = document
                    .Descendants()
                    .Where(element => element.Name.LocalName == "Compile")
                    .Select(element => element.Attribute("Include")?.Value)
                    .Where(include => !string.IsNullOrWhiteSpace(include))
                    .Select(include => Normalize(Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(projectFile)!,
                        include!))))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new ProductionProject(
                    projectPaths[Normalize(Path.GetFullPath(projectFile))],
                    Normalize(Path.GetDirectoryName(Path.GetFullPath(projectFile))!),
                    references,
                    explicitCompileFiles);
            })
            .ToDictionary(project => project.Path, StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertAcyclic(IReadOnlyDictionary<string, ProductionProject> projects)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string project, IReadOnlyList<string> chain)
        {
            if (visited.Contains(project))
                return;
            Assert.True(visiting.Add(project), $"Production project cycle: {string.Join(" -> ", chain.Append(project))}");
            foreach (var reference in projects[project].References)
                Visit(reference, [.. chain, project]);
            visiting.Remove(project);
            visited.Add(project);
        }

        foreach (var project in projects.Keys)
            Visit(project, []);
    }

    private static bool IsGeneratedPath(string path) =>
        Normalize(path).Split('/').Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record ProductionProject(
        string Path,
        string Directory,
        IReadOnlyList<string> References,
        IReadOnlySet<string> ExplicitCompileFiles)
    {
        public bool OwnsSource(string root, string source)
        {
            var absoluteSource = Normalize(System.IO.Path.GetFullPath(System.IO.Path.Combine(root, source)));
            return ExplicitCompileFiles.Contains(absoluteSource) ||
                absoluteSource.StartsWith(Directory + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
