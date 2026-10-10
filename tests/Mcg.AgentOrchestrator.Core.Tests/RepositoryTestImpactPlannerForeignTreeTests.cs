namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryTestImpactPlannerForeignTreeTests
{
    private const string SourcePath = "src/Domain/Widget.cs";
    private const string ManifestNotice =
        "the checks to run are the ones the project's acceptance manifest declares";

    [Xunit.Fact]
    public void ForeignTreeNamesManifestChecksInsteadOfAbsentBuiltInProjects()
    {
        var plan = Plan([SourcePath], new PresentOnlyTree(SourcePath));

        Assert.Contains("none of this tool's built-in test projects", plan.Summary);
        Assert.Contains(ManifestNotice, plan.Summary);
        Assert.DoesNotContain("tests/Mcg.", plan.Summary);
        Assert.DoesNotContain("skipped absent test project", plan.Summary);
        Assert.NotEmpty(plan.Checks);
        Assert.All(plan.Checks, check =>
        {
            Assert.Null(check.TestProject);
            Assert.Empty(check.Command);
            Assert.Contains(ManifestNotice, check.Reason);
            Assert.DoesNotContain("tests/Mcg.", check.Reason);
            Assert.DoesNotContain("skipped absent test project", check.Reason);
        });
    }

    [Xunit.Fact]
    public void ForeignTreeStillNamesRemovedChangedPaths()
    {
        const string removedPath = "src/Domain/Removed.cs";
        var plan = Plan([SourcePath, removedPath], new PresentOnlyTree(SourcePath));
        var removedClause = $"skipped removed path: {removedPath} (absent from candidate tree)";

        Assert.Contains(ManifestNotice, plan.Summary);
        Assert.Contains(removedClause, plan.Summary);
        Assert.All(plan.Checks, check => Assert.Contains(removedClause, check.Reason));
    }

    [Xunit.Fact]
    public void OnlyWholeBuiltInSetAbsenceReplacesBuiltInChecks()
    {
        var foreignPlan = Plan([SourcePath], new PresentOnlyTree(SourcePath));
        var absentProject = RepositoryTestImpactPlanner.BuiltInTestProjectPaths.First();
        var plan = Plan([SourcePath], new AbsentOnlyTree(absentProject));
        var skippedClause = $"skipped absent test project: {absentProject} (absent from candidate tree)";

        Assert.Contains(ManifestNotice, foreignPlan.Summary);
        Assert.Contains(skippedClause, plan.Summary);
        Assert.DoesNotContain("acceptance manifest", plan.Summary);
        Assert.Equal(Enum.GetValues<RepositoryTestProject>().Length - 1, plan.Checks.Count);
        Assert.All(plan.Checks, check =>
        {
            Assert.Contains(skippedClause, check.Reason);
            Assert.DoesNotContain(absentProject, check.Command);
        });
        var allPresentPlan = Plan([SourcePath], CandidateTreeProbe.AssumeAllPresent);
        var expectedProjects = Enum.GetValues<RepositoryTestProject>();

        Assert.Equal(expectedProjects.Length, allPresentPlan.Checks.Count);
        foreach (var project in expectedProjects)
            Assert.Single(allPresentPlan.Checks, check => check.TestProject == project);
        Assert.DoesNotContain("acceptance manifest", allPresentPlan.Summary);
        Assert.DoesNotContain("skipped absent test project", allPresentPlan.Summary);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PartialBuiltInTreeWithoutSolutionKeepsMissingProjectCheck(bool useWorktreeFile)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var missingProject = RepositoryTestImpactPlanner.BuiltInTestProjectPaths.First();
        try
        {
            if (useWorktreeFile)
                File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
            else
                Directory.CreateDirectory(Path.Combine(root, ".git"));

            foreach (var project in RepositoryTestImpactPlanner.BuiltInTestProjectPaths
                .Where(path => path != missingProject))
            {
                var fullPath = Path.Combine(root, project);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, "<Project />");
            }
            var fullSourcePath = Path.Combine(root, SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullSourcePath)!);
            File.WriteAllText(fullSourcePath, "namespace Domain; public class Widget { }");

            Assert.False(File.Exists(Path.Combine(root, missingProject)));
            Assert.DoesNotContain(Directory.EnumerateFiles(root), path =>
                Path.GetExtension(path) is ".sln" or ".slnx");
            Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(root));

            var plan = RepositoryTestImpactPlanner.Plan([SourcePath], root);

            Assert.Single(plan.Checks, check => check.Command.Contains(missingProject));
            Assert.Equal(RepositoryTestImpactPlanner.BuiltInTestProjectPaths.Count, plan.Checks.Count);
            Assert.DoesNotContain("skipped absent test project", plan.Summary);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void DeletedSoleBuiltInProjectWithoutSolutionKeepsMissingProjectCheck(bool useWorktreeFile)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var missingProject = RepositoryTestImpactPlanner.BuiltInTestProjectPaths.First();
        try
        {
            if (useWorktreeFile)
                File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
            else
                Directory.CreateDirectory(Path.Combine(root, ".git"));

            var fullProjectPath = Path.Combine(root, missingProject);
            Directory.CreateDirectory(Path.GetDirectoryName(fullProjectPath)!);
            File.WriteAllText(fullProjectPath, "<Project />");
            File.Delete(fullProjectPath);
            var sourceDirectory = Path.Combine(root, "src", "Domain");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "Domain.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, SourcePath), "namespace Domain; public class Widget { }");

            Assert.True(Directory.Exists(Path.GetDirectoryName(fullProjectPath)));
            Assert.All(RepositoryTestImpactPlanner.BuiltInTestProjectPaths,
                path => Assert.False(File.Exists(Path.Combine(root, path))));
            Assert.DoesNotContain(Directory.EnumerateFiles(root), path =>
                Path.GetExtension(path) is ".sln" or ".slnx");
            Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(root));

            var plan = RepositoryTestImpactPlanner.Plan([SourcePath], root);

            var missingProjectCheck = Assert.Single(plan.Checks, check => check.Command.Contains(missingProject));
            Assert.DoesNotContain("--filter", missingProjectCheck.Command);
            Assert.Equal(RepositoryTestImpactPlanner.BuiltInTestProjectPaths.Count, plan.Checks.Count);
            Assert.DoesNotContain("acceptance manifest", plan.Summary);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static RepositoryTestImpactPlan Plan(string[] paths, ICandidateTreeProbe tree) =>
        RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify(paths),
            UnavailableTestClassDeclarationReader.Instance, tree);

    private sealed class PresentOnlyTree(params string[] presentPaths) : ICandidateTreeProbe
    {
        public bool Exists(string path) => presentPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AbsentOnlyTree(string absentPath) : ICandidateTreeProbe
    {
        public bool Exists(string path) => !path.Equals(absentPath, StringComparison.OrdinalIgnoreCase);
    }
}
