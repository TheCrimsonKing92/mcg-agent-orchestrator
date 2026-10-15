using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: immutable graph inputs and no process/global state.
public sealed class AcceptanceProjectTopologyEligibilityTests
{
    [Fact]
    public void EligibleProjects_AreCompleteAndDependencyOrdered()
    {
        var ordered = OrderProjectTopology(ProjectTopology);
        Assert.Equal(ProjectTopology.Count, ordered.Distinct().Count());
        Assert.Equal(ProjectTopology.Keys.Order(), ordered.Order());
        foreach (var (dependency, entry) in ProjectTopology)
        {
            Assert.True(Enum.IsDefined(entry.CacheEligibility));
            foreach (var consumer in entry.ReferencingProjects)
                Assert.True(ordered.ToList().IndexOf(dependency) < ordered.ToList().IndexOf(consumer),
                    $"Dependency {dependency} must precede {consumer}.");
        }
        var allEligible = CacheEligibleProjects(ProjectTopology.Keys, PolicyShardPlan.NotApplicable("test"));
        Assert.Equal(ordered.Where(project => ProjectTopology[project].CacheEligibility != BaseCacheEligibility.Ineligible), allEligible);
        Assert.Contains(ExecutionProject, allEligible);
    }

    [Fact]
    public void OptionalOrAbsentProjects_RequireTheirEligibilityConditions()
    {
        var absent = PolicyShardPlan.NotApplicable("test") with
        {
            AbsentProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ExecutionTestsProject }
        };
        var eligible = CacheEligibleProjects([ExecutionTestsProject, "unknown.csproj"], absent);
        Assert.DoesNotContain(ExecutionTestsProject, eligible);
        Assert.DoesNotContain("unknown.csproj", eligible);
        foreach (var (project, entry) in ProjectTopology.Where(pair => pair.Value.CacheEligibility == BaseCacheEligibility.WhenTargeted))
            Assert.DoesNotContain(project, eligible);
    }

    [Fact]
    public void InvalidGraph_FailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => OrderProjectTopology(
            new Dictionary<string, ProjectTopologyEntry> { ["a"] = new(BaseCacheEligibility.Ineligible, ["missing"]) }));
        Assert.Throws<InvalidOperationException>(() => OrderProjectTopology(
            new Dictionary<string, ProjectTopologyEntry>
            {
                ["a"] = new(BaseCacheEligibility.Always, ["b"]),
                ["b"] = new(BaseCacheEligibility.Always, ["a"])
            }));
    }

    [Fact]
    public void ProductionMembershipAndKnownEdges_MatchProjectDeclarations()
    {
        var root = RepositoryRoot();
        var productionProjects = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
        Assert.NotEmpty(productionProjects);
        Assert.All(productionProjects, project => Assert.True(ProjectTopology.ContainsKey(project),
            $"Project {project} must declare its topology and explicit cache eligibility."));
        foreach (var consumer in ProjectTopology.Keys)
        {
            var projectFile = Path.Combine(root, consumer);
            Assert.True(File.Exists(projectFile), $"Topology project is missing: {consumer}");
            foreach (var reference in XDocument.Load(projectFile).Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar);
                var dependency = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!, include))).Replace('\\', '/');
                if (ProjectTopology.TryGetValue(dependency, out var entry))
                    Assert.Contains(consumer, entry.ReferencingProjects);
            }
        }
    }

    [Fact]
    public void TargetedEligibility_AcceptsAbsoluteManifestPaths()
    {
        var project = Path.Combine(Path.GetTempPath(), "candidate", ExecutionTestsProject);
        Assert.Contains(ExecutionTestsProject, CacheEligibleProjects([project], PolicyShardPlan.NotApplicable("test")));
    }

    private static string RepositoryRoot([CallerFilePath] string source = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)) return root;
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Topology verification repository is missing.");
    }
}
