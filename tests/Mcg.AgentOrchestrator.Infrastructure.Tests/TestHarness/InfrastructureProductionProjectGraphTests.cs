using System.Xml.Linq;
using System.Text.Json;

public sealed class InfrastructureProductionProjectGraphTests
{
    private const string ProvidersProject =
        "src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj";
    private const string OperatorCommsProject =
        "src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj";
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

    [Xunit.Fact(DisplayName = "OperatorComms production assembly owns its seam once with Core-only dependency direction")]
    public void OperatorCommsAssemblyOwnsOperatorCommsSources()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var projects = LoadProductionProjects(root);

        Assert.True(
            projects.TryGetValue(OperatorCommsProject, out var operatorComms),
            $"Missing production project: {OperatorCommsProject}");
        Assert.Equal([CoreProject], operatorComms.References);
        Assert.DoesNotContain(OperatorCommsProject, projects[InfrastructureProject].References);
        Assert.Contains(OperatorCommsProject, projects[AppProject].References);
        AssertAcyclic(projects);

        var operatorCommsSources = Directory
            .EnumerateFiles(
                Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure.OperatorComms"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Select(path => Normalize(Path.GetRelativePath(root, path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(operatorCommsSources);

        foreach (var source in operatorCommsSources)
        {
            var owners = projects.Values
                .Where(project => project.OwnsSource(root, source))
                .Select(project => project.Path)
                .ToArray();
            Assert.Equal([OperatorCommsProject], owners);
        }
    }

    [Xunit.Fact(DisplayName = "Production project references do not fork shared-output graph nodes with global properties")]
    public void ProductionProjectReferencesDoNotForkSharedOutputGraphNodes()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var projectFiles = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path));

        foreach (var projectFile in projectFiles)
        {
            var document = XDocument.Load(projectFile, LoadOptions.SetLineInfo);
            foreach (var reference in document.Descendants()
                         .Where(element => element.Name.LocalName == "ProjectReference"))
            {
                Assert.Null(reference.Attribute("AdditionalProperties"));
            }
        }
    }

    [Xunit.Fact]
    public void AcceptanceOwnerProjectIsManifestDeclaredAndIsolated()
    {
        const string project =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj";
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var projectPath = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
        var document = XDocument.Load(projectPath);
        var references = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")!.Value)
            .Select(include => Normalize(Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(projectPath)!, include)))))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal([CoreProject, InfrastructureProject], references);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "acceptance-manifest.json")));
        var declaredChecks = manifest.RootElement.GetProperty("checks").EnumerateArray()
            .Count(check => check.TryGetProperty("project", out var value) && value.GetString() == project);
        var declaredInvocations = manifest.RootElement.GetProperty("engine").GetProperty("mtpInvocations").EnumerateArray()
            .Count(invocation => invocation.GetProperty("project").GetString() == project);
        Assert.Equal(1, declaredChecks);
        Assert.Equal(1, declaredInvocations);

        var umbrella = XDocument.Load(Path.Combine(
            root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        Assert.Contains(umbrella.Descendants(), element =>
            element.Name.LocalName == "Compile" &&
            element.Attribute("Remove")?.Value == "Acceptance\u005c**\u005c*.cs");
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
