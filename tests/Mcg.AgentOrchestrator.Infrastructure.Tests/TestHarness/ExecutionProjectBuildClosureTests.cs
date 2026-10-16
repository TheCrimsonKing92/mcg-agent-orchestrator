using System.Xml.Linq;

// Parallel-safe: reads project files; negative controls mutate only in-memory XML.
public sealed class ExecutionProjectBuildClosureTests
{
    [Fact]
    public void BuildClosure_CurrentProject_ContainsOnlyExecutionDependencies()
    {
        var projectPath = ProjectPath();
        var closure = Closure(XDocument.Load(projectPath), projectPath, XDocument.Load);

        Assert.True(closure.SetEquals([
            "Mcg.AgentOrchestrator.Core",
            "Mcg.AgentOrchestrator.Execution",
            "Mcg.AgentOrchestrator.Infrastructure.Providers"
        ]), $"Unexpected project closure: {string.Join(", ", closure.Order(StringComparer.Ordinal))}");
        Assert.DoesNotContain("Mcg.AgentOrchestrator.App", closure);
        Assert.DoesNotContain("Mcg.AgentOrchestrator.Infrastructure", closure);
    }

    [Fact]
    public void BuildClosure_AddedInfrastructureReference_IncludesInfrastructure()
    {
        var projectPath = ProjectPath();
        var project = new XDocument(XDocument.Load(projectPath));
        project.Root!.Add(new XElement("ItemGroup", new XElement("ProjectReference",
            new XAttribute("Include",
                @"..\..\..\src\Mcg.AgentOrchestrator.Infrastructure\Mcg.AgentOrchestrator.Infrastructure.csproj"))));

        var closure = Closure(project, projectPath, XDocument.Load);

        Assert.Contains("Mcg.AgentOrchestrator.Infrastructure", closure);
    }

    private static string ProjectPath() => Path.Combine(
        InfrastructureTestSupport.FindRepositoryRoot(), "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests",
        "Execution", "Mcg.AgentOrchestrator.Infrastructure.Execution.Tests.csproj");

    private static IReadOnlySet<string> Closure(
        XDocument root, string rootProjectPath, Func<string, XDocument> load)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(rootProjectPath)
        };
        var projects = new HashSet<string>(StringComparer.Ordinal);
        Visit(root, Path.GetDirectoryName(rootProjectPath)!);
        return projects;

        void Visit(XDocument project, string directory)
        {
            foreach (var reference in project.Descendants().Where(element =>
                         element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (include is null)
                    continue;

                var path = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/')));
                if (!visited.Add(path))
                    continue;

                projects.Add(Path.GetFileNameWithoutExtension(path));
                Visit(load(path), Path.GetDirectoryName(path)!);
            }
        }
    }
}
