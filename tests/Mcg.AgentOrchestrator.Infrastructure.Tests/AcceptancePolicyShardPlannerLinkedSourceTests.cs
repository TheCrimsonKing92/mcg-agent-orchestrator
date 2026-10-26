using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: read-only project inspection; the control changes only an in-memory document.
public sealed class AcceptancePolicyShardPlannerLinkedSourceTests
{
    private static readonly AcceptanceShardPolicySwitches Scoped = new(FullShards: false, ChangeScoped: true);
    private static string Root => InfrastructureTestSupport.FindRepositoryRoot();
    private static string MainDirectory => Path.Combine(Root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests");

    [Fact]
    public void DeclaredLinks_ExactlyMatchConsumerTable()
    {
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Directory.EnumerateFiles(MainDirectory, "*.csproj", SearchOption.AllDirectories)
                     .Where(project => Path.GetDirectoryName(project) != MainDirectory &&
                         !Path.GetRelativePath(MainDirectory, project).Replace('\\', '/').Split('/')
                             .Any(segment => segment is "Fixtures" or "bin" or "obj")))
            actual.UnionWith(LinkedPairs(project, XDocument.Load(project)));

        var declared = DeclaredPairs();
        Assert.NotEmpty(actual);
        Assert.Empty(actual.Except(declared, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(declared.Except(actual, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedHelperChange_KeepsOwnerAndConsumer()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureTestSupport.cs"], Scoped);
        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.True(plan.IncludesProject(AcceptancePolicyShardPlanner.InfrastructureTestsProject));
        Assert.True(plan.IncludesProject(AcceptancePolicyShardPlanner.StorageRetentionTestsProject));
    }

    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyTempRedirect.cs")]
    [InlineData("tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\ASSEMBLYTEMPREDIRECT.cs")]
    public void SharedRedirectChange_ReachesEveryNestedConsumer(string path)
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([path], Scoped);
        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        foreach (var project in new[] { AcceptancePolicyShardPlanner.CliTestsProject,
                     AcceptancePolicyShardPlanner.ExecutionTestsProject, AcceptancePolicyShardPlanner.AcceptanceTestsProject,
                     AcceptancePolicyShardPlanner.ProviderEnvironmentTestsProject, AcceptancePolicyShardPlanner.StorageRetentionTestsProject })
            Assert.True(plan.IncludesProject(project), project);
    }

    [Fact]
    public void ExtraExternalLink_ReportsExactlyMissingPair()
    {
        var project = Path.Combine(Root, AcceptancePolicyShardPlanner.StorageRetentionTestsProject);
        var document = XDocument.Load(project);
        document.Root!.Add(new XElement("ItemGroup", new XElement("Compile",
            new XAttribute("Include", "..\\WorkerProfileTests.cs"))));
        var missing = LinkedPairs(project, document).Except(DeclaredPairs(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs|" +
            AcceptancePolicyShardPlanner.StorageRetentionTestsProject], missing);
    }

    private static IEnumerable<string> DeclaredPairs() =>
        AcceptancePolicyShardPlannerLinkedSources.Table.SelectMany(row => row.Value.Select(project => row.Key + "|" + project));

    private static IEnumerable<string> LinkedPairs(string project, XDocument document)
    {
        var directory = Path.GetDirectoryName(project)!;
        foreach (var include in document.Descendants("Compile").Attributes("Include"))
        {
            Assert.DoesNotContain('*', include.Value);
            Assert.DoesNotContain('?', include.Value);
            var target = Path.GetFullPath(Path.Combine(directory, include.Value.Replace('\\', '/')));
            var relative = Path.GetRelativePath(directory, target).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                yield return Path.GetRelativePath(Root, target).Replace('\\', '/') + "|" +
                    Path.GetRelativePath(Root, project).Replace('\\', '/');
        }
    }
}
