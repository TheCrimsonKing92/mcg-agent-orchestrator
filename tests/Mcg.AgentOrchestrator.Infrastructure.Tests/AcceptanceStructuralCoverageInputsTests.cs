using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: uses literal diff inputs and a unique temporary worktree owned by each test.
public sealed class AcceptanceStructuralCoverageInputsTests
{
    private const string Project = "tests/A.Tests/A.Tests.csproj";

    [Fact]
    public void NameStatusRows_PreserveRemovedFilesAndOrderedUnresolvedRenames()
    {
        var output = string.Join('\n',
            "D\ttests/A.Tests/OldTests.cs",
            "M\ttests/A.Tests/EditedTests.cs",
            "M\tsrc/A/Widget.cs",
            "D\tsrc/A/Widget.cs",
            "R100\ttests/A.Tests/MovedTests.cs\ttests/B.Tests/MovedTests.cs",
            "R095\ttests/A.Tests/StayTests.cs\ttests/A.Tests/Sub/StayTests.cs",
            "R100\ttests/A.Tests/LostTests.cs\traw/ZTests.cs",
            "R100\ttests/A.Tests/LostTests.cs\traw/ZTests.cs",
            "R100\ttests/A.Tests/LostTests.cs\traw/ATests.cs",
            "R100\ttests/A.Tests/AlphaTests.cs\traw/AlphaTests.cs");
        var owners = new Dictionary<string, string>
        {
            ["tests/B.Tests/MovedTests.cs"] = "tests/B.Tests/B.Tests.csproj",
            ["tests/A.Tests/Sub/StayTests.cs"] = Project
        };
        string? ResolveOwner(string path) => owners.GetValueOrDefault(path);

        var parsed = AcceptanceStructuralCoverageInputs.ParseDeletedTestFiles(output, Project, ResolveOwner);

        Assert.Equal(["tests/A.Tests/OldTests.cs", "tests/A.Tests/MovedTests.cs"], parsed.Removed);
        Assert.Equal(
            new[]
            {
                new UnresolvedRenameDestination("tests/A.Tests/AlphaTests.cs", "raw/AlphaTests.cs"),
                new UnresolvedRenameDestination("tests/A.Tests/LostTests.cs", "raw/ATests.cs"),
                new UnresolvedRenameDestination("tests/A.Tests/LostTests.cs", "raw/ZTests.cs")
            },
            parsed.UnresolvedRenames);

        var forwarded = GoalAcceptanceVerifier.ParseDeletedTestFilesWithUnresolvedRenamesForTests(
            output, Project, ResolveOwner);
        Assert.Equal(parsed.Removed, forwarded.Removed);
        Assert.Equal(parsed.UnresolvedRenames, forwarded.UnresolvedRenames);
        Assert.Equal(parsed.Removed, GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(output, Project, ResolveOwner));
    }

    [Fact]
    public void WorktreeProjects_DiscoverTrustedProjectsAndResolveContainedOwners()
    {
        var root = Path.Combine(Path.GetTempPath(), "structural-coverage-inputs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WriteProject(root, "tests/Support/Support.csproj", "<Project />");
            WriteProject(root, "tests/Helpers/Helpers.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            WriteProject(root, Project, "<Project />");

            var projects = AcceptanceStructuralCoverageInputs.DiscoverTrustedTestProjects(root);
            Assert.Equal(new[] { Project, "tests/Helpers/Helpers.csproj" }, projects);
            Assert.Equal(projects, GoalAcceptanceVerifier.DiscoverTrustedTestProjects(root));
            Assert.Equal(projects, AcceptanceStructuralCoverageInputs.DiscoverTrustedTestProjects(root, root));
            Assert.Equal(projects, GoalAcceptanceVerifier.DiscoverTrustedTestProjects(root, root));

            foreach (var path in new[] { "tests/A.Tests/Nested/XTests.cs", "../outside/XTests.cs", " " })
            {
                var owner = AcceptanceStructuralCoverageInputs.ResolveOwningProject(root, path);
                if (path == "tests/A.Tests/Nested/XTests.cs")
                    Assert.Equal(Project, owner);
                else
                    Assert.Null(owner);

                Assert.Equal(owner, GoalAcceptanceVerifier.ResolveOwningProjectForTests(root, path));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectFilters_KeepOnlyFilesAndRenamesWithinTheProject()
    {
        string[] files = ["tests/A.Tests/OldTests.cs", "tests/B.Tests/OtherTests.cs", "tests/A.TestsExtra/OtherTests.cs"];
        var filtered = AcceptanceStructuralCoverageInputs.DeletedTestFilesForProject(files, Project);
        Assert.Equal(new[] { files[0] }, filtered);
        Assert.Equal(filtered, GoalAcceptanceVerifier.DeletedTestFilesForProject(files, Project));

        UnresolvedRenameDestination[] rows =
        [
            new(files[0], "raw/OldTests.cs"),
            new(files[1], "raw/OtherTests.cs"),
            new(files[2], "raw/ExtraTests.cs")
        ];
        var renames = AcceptanceStructuralCoverageInputs.UnresolvedRenamesForProject(rows, Project);
        Assert.Equal(new[] { rows[0] }, renames);
        Assert.Equal(renames, GoalAcceptanceVerifier.UnresolvedRenamesForProject(rows, Project));
    }

    private static void WriteProject(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
