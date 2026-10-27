using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its temporary repository and uses no shared process or mutable state.
public sealed class ForeignTargetTestImpactPlannerTests
{
    [Fact]
    public void GoSource_SuppressesBuiltInProjectsAndDeclaresManifestChecks()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            string[] paths = ["internal/widget/widget.go"];
            var existingPlan = RepositoryTestImpactPlanner.Plan(paths, root);
            Assert.Contains(existingPlan.Checks, check =>
                check.CommandLine.Contains("tests/Mcg.AgentOrchestrator", StringComparison.Ordinal));

            var plan = ForeignTargetTestImpactPlanner.Plan(paths, root);

            AssertForeignPlan(plan);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void NodeSourceAndPackage_SuppressBuiltInProjectsAndDeclareManifestChecks()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
            File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"widget\"}");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "index.ts"), "export const widget = 1;");

            var plan = ForeignTargetTestImpactPlanner.Plan(["package.json", "src/index.ts"], root);

            AssertForeignPlan(plan);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void PythonWithoutWorktreeMarker_SuppressesBuiltInProjectsAndDeclaresManifestChecks()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "main.py"), "print('widget')");

            var plan = ForeignTargetTestImpactPlanner.Plan(["main.py"], root);

            AssertForeignPlan(plan);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void ForeignDotNetSource_PreservesExistingPlan()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        const string sourcePath = "src/Domain/Widget.cs";
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
            var projectDirectory = Path.Combine(root, "src", "Domain");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Domain.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, sourcePath), "namespace Domain; public class Widget { }");

            var expected = RepositoryTestImpactPlanner.Plan([sourcePath], root);
            var actual = ForeignTargetTestImpactPlanner.Plan([sourcePath], root);

            AssertPlansEqual(expected, actual);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void GoDocumentationOnly_PreservesExistingPlan()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            File.WriteAllText(Path.Combine(root, "README.md"), "# Widget");

            var expected = RepositoryTestImpactPlanner.Plan(["README.md"], root);
            var actual = ForeignTargetTestImpactPlanner.Plan(["README.md"], root);

            AssertPlansEqual(expected, actual);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void GoWithoutChangedPaths_PreservesExistingPlan()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);

            var expected = RepositoryTestImpactPlanner.Plan(Array.Empty<string>(), root);
            var actual = ForeignTargetTestImpactPlanner.Plan(Array.Empty<string>(), root);

            AssertPlansEqual(expected, actual);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void WriteGoFixture(string root)
    {
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/widget\n");
        var directory = Path.Combine(root, "internal", "widget");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "widget.go"), "package widget\n");
    }

    private static void AssertForeignPlan(RepositoryTestImpactPlan plan)
    {
        Assert.NotEmpty(plan.Checks);
        Assert.DoesNotContain("tests/Mcg.", plan.Summary);
        Assert.Contains("the checks to run are the ones the project's acceptance manifest declares", plan.Summary);
        Assert.All(plan.Checks, check =>
        {
            Assert.Empty(check.Command);
            Assert.Null(check.TestProject);
            Assert.DoesNotContain("tests/Mcg.", check.Reason);
            Assert.DoesNotContain("tests/Mcg.", check.CommandLine);
        });
    }

    private static void AssertPlansEqual(RepositoryTestImpactPlan expected, RepositoryTestImpactPlan actual)
    {
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.RequiresBuild, actual.RequiresBuild);
        Assert.Equal(expected.RequiresBroadVerification, actual.RequiresBroadVerification);
        Assert.Equal(expected.Checks.Count, actual.Checks.Count);
        for (var index = 0; index < expected.Checks.Count; index++)
        {
            Assert.Equal(expected.Checks[index].Name, actual.Checks[index].Name);
            Assert.Equal(expected.Checks[index].CommandLine, actual.Checks[index].CommandLine);
            Assert.Equal(expected.Checks[index].Reason, actual.Checks[index].Reason);
            Assert.Equal(expected.Checks[index].TestProject, actual.Checks[index].TestProject);
        }
    }
}
