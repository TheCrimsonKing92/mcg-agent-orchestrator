using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every test owns a temporary repository and explicit planner root.
public sealed class RepositoryTestImpactPlannerOwnerConsoleRouteTests
{
    private const string Prefix = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsole/";
    private const string Project = Prefix + "Mcg.AgentOrchestrator.Infrastructure.OwnerConsole.Tests.csproj";
    private const string MovedPath = Prefix + "OwnerConsoleBoardTests.cs";
    private const string MainPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs";
    private const string OtherPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StorageRetention/StorageRetentionResumeCursorTests.cs";

    [Fact]
    public void NestedFile_SelectsExactFocusedCommand()
    {
        using var repository = new Repository();
        var check = Assert.Single(repository.Plan(MovedPath).Checks);
        Assert.Equal(RepositoryTestProject.OwnerConsole, check.TestProject);
        Assert.Equal("focused changed owner console tests", check.Name);
        Assert.Equal(new[] { "dotnet", "test", "--project", Project, "--verbosity", "minimal", "--filter",
            "FullyQualifiedName~OwnerConsoleBoardTests" }, check.Command);
    }

    [Fact]
    public void MixedFiles_KeepProjectFiltersSeparate()
    {
        using var repository = new Repository();
        var plan = repository.Plan(MovedPath, MainPath);
        Assert.Equal(2, plan.Checks.Count);
        var nested = Assert.Single(plan.Checks.Where(check => check.TestProject == RepositoryTestProject.OwnerConsole));
        Assert.Equal("FullyQualifiedName~OwnerConsoleBoardTests", nested.Command.Last());
        var main = Assert.Single(plan.Checks.Where(check => check.TestProject == RepositoryTestProject.Infrastructure));
        Assert.Contains("--filter", main.Command);
        Assert.Equal("FullyQualifiedName~WorkerProfileTests", main.Command.Last());
        Assert.DoesNotContain("OwnerConsoleBoardTests", main.Command.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildChange_AppendsWholeProjectAndInventory()
    {
        using var repository = new Repository();
        repository.Write("Directory.Build.props", "<Project />");
        var plan = repository.Plan("Directory.Build.props");
        Assert.True(plan.RequiresBroadVerification);
        Assert.Equal(new[] { "owner console tests", "storage retention tests" },
            plan.Checks.TakeLast(2).Select(check => check.Name));
        var last = plan.Checks[^2];
        Assert.Equal("owner console tests", last.Name);
        Assert.Equal(RepositoryTestProject.OwnerConsole, last.TestProject);
        Assert.Equal(new[] { "dotnet", "test", "--project", Project, "--verbosity", "minimal" }, last.Command);
        Assert.Contains(Project, RepositoryTestImpactPlanner.BuiltInTestProjectPaths);
    }

    [Fact]
    public void OtherNestedFolder_DoesNotSelectNewOwner()
    {
        using var repository = new Repository();
        var plan = repository.Plan(OtherPath);
        Assert.NotEmpty(plan.Checks);
        Assert.DoesNotContain(plan.Checks, check => check.TestProject == RepositoryTestProject.OwnerConsole);
    }

    [Fact]
    public void UnresolvedFile_FallsBackToWholeProject()
    {
        using var repository = new Repository();
        repository.Write(MovedPath, "public class Helper {}");
        var check = Assert.Single(repository.Plan(MovedPath).Checks);
        Assert.Equal(RepositoryTestProject.OwnerConsole, check.TestProject);
        Assert.Equal("owner console tests", check.Name);
        Assert.Equal(new[] { "dotnet", "test", "--project", Project, "--verbosity", "minimal" }, check.Command);
        Assert.Null(check.TestClassSelections);
    }

    [Fact]
    public void ResolvedAndHelperFiles_KeepResolvedAggregate()
    {
        using var repository = new Repository();
        const string helper = Prefix + "Helper.cs";
        repository.Write(helper, "public class Helper {}");
        var check = Assert.Single(repository.Plan(MovedPath, helper).Checks);
        Assert.Equal(RepositoryTestProject.OwnerConsole, check.TestProject);
        Assert.Equal("FullyQualifiedName~OwnerConsoleBoardTests", check.Command.Last());
        Assert.Equal(["OwnerConsoleBoardTests"], check.TestClassSelections);
    }

    private sealed class Repository : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-owner-console-route-{Guid.NewGuid():N}");

        internal Repository()
        {
            foreach (var project in RepositoryTestImpactPlanner.BuiltInTestProjectPaths)
                Write(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(MovedPath, "public class OwnerConsoleBoardTests { [Xunit.Fact] public void Example() {} }");
            Write(MainPath, "public class " + Path.GetFileNameWithoutExtension(MainPath) + " { [Xunit.Fact] public void Example() {} }");
            Write(OtherPath, "public class StorageRetentionResumeCursorTests { [Xunit.Fact] public void Example() {} }");
        }

        internal RepositoryTestImpactPlan Plan(params string[] paths) =>
            RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify(paths), Root);

        internal void Write(string relative, string text)
        {
            var absolute = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
