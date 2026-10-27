using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary repository and uses no shared process or mutable state.
public sealed class WorkerTestImpactPlanSelectionTests
{
    [Fact]
    public void HomeTarget_UsesExistingPlanner()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            string[] paths = ["internal/widget/widget.go"];

            var expected = RepositoryTestImpactPlanner.Plan(paths, root);
            var actual = WorkerTestImpactPlanSelection.For(paths, root, WorkerTargetHome.Home);

            AssertPlansEqual(expected, actual);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void NonHomeTarget_UsesForeignPlannerWithoutBuiltInCommands()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            string[] paths = ["internal/widget/widget.go"];

            var expected = ForeignTargetTestImpactPlanner.Plan(paths, root);
            var actual = WorkerTestImpactPlanSelection.For(paths, root, WorkerTargetHome.NotHome);

            AssertPlansEqual(expected, actual);
            Assert.NotEmpty(actual.Checks);
            Assert.All(actual.Checks, check => Assert.Empty(check.Command));
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
