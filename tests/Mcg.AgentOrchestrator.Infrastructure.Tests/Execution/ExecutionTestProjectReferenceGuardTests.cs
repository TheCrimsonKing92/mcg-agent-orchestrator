using System.Runtime.CompilerServices;
using System.Xml.Linq;

// Parallel-safe: reads the verification worktree; negative controls mutate only in-memory XML.
public sealed class ExecutionTestProjectReferenceGuardTests
{
    private const string ProjectFile = "Mcg.AgentOrchestrator.Infrastructure.Execution.Tests.csproj";

    [Fact]
    public void ProjectReferences_CurrentProject_ContainsOnlyCoreAndExecution()
    {
        Assert.True(HasExpectedReferences(File.ReadAllText(ProjectPath())));
    }

    [Fact]
    public void ProjectReferences_AddedInfrastructureReference_IsRejected()
    {
        var project = XDocument.Load(ProjectPath());
        Assert.True(HasExpectedReferences(project.ToString()));
        project.Root!.Add(new XElement("ItemGroup", new XElement("ProjectReference",
            new XAttribute("Include", @"..\..\..\src\Mcg.AgentOrchestrator.Infrastructure\Mcg.AgentOrchestrator.Infrastructure.csproj"))));

        Assert.False(HasExpectedReferences(project.ToString()));
    }

    [Fact]
    public void ProjectReferences_MissingExecutionReference_IsRejected()
    {
        var project = XDocument.Load(ProjectPath());
        Assert.True(HasExpectedReferences(project.ToString()));
        Assert.Single(project.Descendants("ProjectReference").Where(reference =>
            ProjectName(reference) == "Mcg.AgentOrchestrator.Execution")).Remove();

        Assert.False(HasExpectedReferences(project.ToString()));
    }

    private static bool HasExpectedReferences(string projectXml) =>
        XDocument.Parse(projectXml).Descendants("ProjectReference").Select(ProjectName)
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(["Mcg.AgentOrchestrator.Core", "Mcg.AgentOrchestrator.Execution"]);

    private static string ProjectName(XElement reference) =>
        Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/'));

    private static string ProjectPath([CallerFilePath] string sourceFile = "")
    {
        // Cache-restored assemblies can carry a source path from a different worktree.
        var candidate = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                var root = Path.GetFullPath(candidate);
                var marker = Path.Combine(root, ".git");
                if ((Directory.Exists(marker) || File.Exists(marker)) &&
                    File.Exists(Path.Combine(root, "Mcg.AgentOrchestrator.sln")) &&
                    Directory.Exists(Path.Combine(root, "tests")))
                    return Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Execution", ProjectFile);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Invalid overrides retain the source-path fallback, like VerifiedRepositoryRoot.
            }
        }

        return Path.Combine(Path.GetDirectoryName(sourceFile)!, ProjectFile);
    }
}
