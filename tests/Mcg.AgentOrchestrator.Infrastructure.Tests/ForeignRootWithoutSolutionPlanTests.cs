using Mcg.AgentOrchestrator.Core;

public sealed class ForeignRootWithoutSolutionPlanTests
{
    [Fact]
    public void ProjectRootWithoutSolutionPlansManifestChecksWithoutBuiltInPaths()
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

            var plan = RepositoryTestImpactPlanner.Plan(
                RepositoryChangeClassifier.Classify([sourcePath]), root);

            Assert.DoesNotContain("tests/Mcg.", plan.Summary);
            Assert.DoesNotContain("skipped absent test project", plan.Summary);
            Assert.Contains("the checks to run are the ones the project's acceptance manifest declares", plan.Summary);
            Assert.NotEmpty(plan.Checks);
            Assert.All(plan.Checks, check =>
            {
                Assert.Null(check.TestProject);
                Assert.Empty(check.Command);
                Assert.DoesNotContain("tests/Mcg.", check.Reason);
                Assert.DoesNotContain("tests/Mcg.", check.CommandLine);
            });
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
