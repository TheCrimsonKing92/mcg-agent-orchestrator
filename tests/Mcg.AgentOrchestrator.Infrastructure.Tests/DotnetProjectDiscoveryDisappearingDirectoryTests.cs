using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: filesystem operations are synchronous and every test owns its repository and traversal seam.
public sealed class DotnetProjectDiscoveryDisappearingDirectoryTests
{
    private static readonly string[] ExpectedUnits = ["src/Alpha/Alpha.csproj", "src/Zeta/Zeta.csproj"];

    [Theory]
    [InlineData("FileNotFoundException")]
    [InlineData("DirectoryNotFoundException")]
    public void AttributeMissingChild_PreservesUnitsAndReportsPath(string exceptionType)
    {
        using var repo = new Repository();
        var baseline = new DotnetProjectDiscoveryAdapter().Discover(repo.Root);
        repo.Write("src/vanished/Lost.csproj", "<Project />");
        var traversal = MissingAt(repo, "src/vanished", "attributes", Missing(exceptionType));

        var model = new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root);

        AssertIncomplete(model, baseline, "src/vanished");
    }

    [Theory]
    [InlineData("files")]
    [InlineData("directories")]
    public void RecursiveEntryMissingChild_PreservesUnitsAndReportsPath(string operation)
    {
        using var repo = new Repository();
        var baseline = new DotnetProjectDiscoveryAdapter().Discover(repo.Root);
        repo.Write("src/vanished/Lost.csproj", "<Project />");
        // Windows maps missing directory-open ERROR_FILE_NOT_FOUND to ERROR_PATH_NOT_FOUND.
        // Exercise deferred MoveNext failure as DirectoryNotFoundException at both immediate-list boundaries.
        var traversal = MissingAt(repo, "src/vanished", operation, new DirectoryNotFoundException("missing child"));

        var model = new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root);

        AssertIncomplete(model, baseline, "src/vanished");
    }

    [Theory]
    [InlineData("attributes")]
    [InlineData("files")]
    [InlineData("directories")]
    public void GrandchildMissing_ReportsOnlyGrandchildPath(string operation)
    {
        using var repo = new Repository();
        var baseline = new DotnetProjectDiscoveryAdapter().Discover(repo.Root);
        repo.Write("src/parent/vanished/Lost.csproj", "<Project />");
        var traversal = MissingAt(repo, "src/parent/vanished", operation, new DirectoryNotFoundException("missing grandchild"));

        var model = new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root);

        AssertIncomplete(model, baseline, "src/parent/vanished");
    }

    [Theory]
    [InlineData("attributes", false)]
    [InlineData("attributes", true)]
    [InlineData("files", false)]
    [InlineData("files", true)]
    [InlineData("directories", false)]
    [InlineData("directories", true)]
    public void DirectoryError_PreservesVisibleFailure(string operation, bool accessDenied)
    {
        using var repo = new Repository();
        Directory.CreateDirectory(repo.PathFor("src/unreadable"));
        Exception failure = accessDenied ? new UnauthorizedAccessException("denied") : new IOException("not missing");
        var traversal = MissingAt(repo, "src/unreadable", operation, failure);

        Assert.Same(failure, Record.Exception(() => new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root)));
    }

    [Theory]
    [InlineData("files")]
    [InlineData("directories")]
    public void RootEnumerationMissing_PreservesVisibleFailure(string operation)
    {
        using var repo = new Repository();
        var failure = new DirectoryNotFoundException("root vanished");
        var traversal = new Traversal
        {
            Before = (current, path) =>
            {
                if (current == operation && path == repo.Root)
                    throw failure;
            }
        };

        Assert.Same(failure, Record.Exception(() => new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root)));
    }

    [Theory]
    [InlineData("files")]
    [InlineData("directories")]
    public void RootDisappearsDuringEnumeration_ReportsRootError(string operation)
    {
        using var repo = new Repository();
        var traversal = new Traversal
        {
            Before = (current, path) =>
            {
                if (current == operation && path == repo.Root)
                    repo.MoveRoot();
            }
        };

        var failure = Record.Exception(() => new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root));

        Assert.IsType<DirectoryNotFoundException>(failure);
        Assert.Contains(repo.Root, failure.Message);
    }

    [Theory]
    [InlineData("attributes")]
    [InlineData("files")]
    [InlineData("directories")]
    public void RootDisappearsWithChild_ReportsRootError(string operation)
    {
        using var repo = new Repository();
        Directory.CreateDirectory(repo.PathFor("src/vanished"));
        var traversal = MissingAt(repo, "src/vanished", operation, new FileNotFoundException("child missing"), repo.MoveRoot);

        var failure = Record.Exception(() => new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root));

        Assert.True(failure is FileNotFoundException or DirectoryNotFoundException, failure?.ToString());
        Assert.Contains(repo.Root, failure!.Message);
    }

    [Fact]
    public void RootDisappearsAfterLastChildLists_ReportsRootError()
    {
        using var repo = new Repository();
        Directory.CreateDirectory(repo.PathFor("zz-last"));
        var traversal = new Traversal
        {
            // Arrange the loss after the last directory's immediate lists have both been read.
            Transform = (operation, _, entries) => operation == "directories"
                ? entries.Order(StringComparer.Ordinal) : entries,
            After = (operation, path) =>
            {
                if (operation == "directories" && path == repo.PathFor("zz-last"))
                    repo.MoveRoot();
            }
        };

        var failure = Record.Exception(() => new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root));

        Assert.True(failure is FileNotFoundException or DirectoryNotFoundException, failure?.ToString());
        Assert.Contains(repo.Root, failure!.Message);
        Assert.DoesNotContain(repo.PathFor("zz-last"), failure.Message);
    }

    [Theory]
    [InlineData("FileNotFoundException")]
    [InlineData("DirectoryNotFoundException")]
    public void FileAttributeMissing_PreservesVisibleFailure(string exceptionType)
    {
        using var repo = new Repository();
        var failure = Missing(exceptionType);
        var traversal = new Traversal
        {
            Before = (operation, path) =>
            {
                if (operation == "attributes" && path == repo.PathFor(ExpectedUnits[0]))
                    throw failure;
            }
        };

        Assert.Same(failure, Record.Exception(() => new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root)));
    }

    [Fact]
    public void FileLostAfterAttributes_RetainsReferencedUnitAndUncertainty()
    {
        using var repo = new Repository();
        var traversal = new Traversal
        {
            After = (operation, path) =>
            {
                if (operation == "attributes" && path == repo.PathFor(ExpectedUnits[0]))
                    File.Delete(path);
            }
        };

        var model = new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root);

        Assert.Equal(ExpectedUnits, model.Units.Select(unit => unit.Id));
        var unit = model.Units[0];
        Assert.Equal(FactConfidence.Low, unit.Location.Confidence);
        Assert.Equal(new FactSource("Sample.slnx", 2), unit.Location.Source);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"units/{unit.Id}/location");
        Assert.Empty(DirectoryQuestions(model));
    }

    [Fact]
    public void ExcludedDirectories_AreSkippedBeforeInspection()
    {
        using var repo = new Repository();
        var baseline = new DotnetProjectDiscoveryAdapter().Discover(repo.Root);
        string[] excluded = ["bin", "obj", ".git", "Owner-Cache"];
        foreach (var directory in excluded)
            repo.Write($"{directory}/Noise.csproj", "<Project />");
        var traversal = new Traversal
        {
            Before = (_, path) =>
            {
                if (excluded.Any(directory => path == repo.PathFor(directory)))
                    throw new UnauthorizedAccessException("excluded directory was inspected");
            }
        };

        var model = new DotnetProjectDiscoveryAdapter(traversal).Discover(repo.Root, ["OWNER-CACHE"]);

        Assert.Equal(ProjectModelJson.Serialize(baseline), ProjectModelJson.Serialize(model));
    }

    [Theory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void MissingSolutionMember_RetainsUnitAndSolutionProvenance(string extension)
    {
        using var repo = new Repository();
        File.Delete(repo.PathFor("Sample.slnx"));
        repo.Write($"Sample.{extension}", extension == "slnx"
            ? "<Solution>\n<Project Path=\"missing/Listed.csproj\" />\n</Solution>"
            : "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
              "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Listed\", \"missing\\Listed.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject");

        var model = new DotnetProjectDiscoveryAdapter().Discover(repo.Root);

        Assert.Equal(new[] { "missing/Listed.csproj" }.Concat(ExpectedUnits), model.Units.Select(unit => unit.Id));
        var unit = model.Units[0];
        Assert.Equal(FactConfidence.Low, unit.Location.Confidence);
        Assert.Equal(new FactSource($"Sample.{extension}", 2), unit.Location.Source);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "units/missing/Listed.csproj/location");
        Assert.Empty(DirectoryQuestions(model));
    }

    [Fact]
    public void RepeatedDiscovery_PreservesOrderedIdentities()
    {
        using var first = new Repository();
        using var second = new Repository();
        var baseline = new DotnetProjectDiscoveryAdapter().Discover(first.Root);
        var snapshots = new List<string>();
        foreach (var repo in new[] { first, second })
        {
            repo.Write("src/vanished/Lost.csproj", "<Project />");
            repo.Write("src/also-missing/Lost.csproj", "<Project />");
            var traversal = MissingAt(repo, "src/vanished", "attributes", new FileNotFoundException("missing"));
            var before = traversal.Before;
            traversal.Before = (operation, path) =>
            {
                before!(operation, path);
                if (operation == "attributes" && path == repo.PathFor("src/also-missing"))
                {
                    Assert.Contains(path, traversal.ObservedDirectories);
                    throw new DirectoryNotFoundException("also missing");
                }
            };
            traversal.Transform = (operation, _, entries) =>
            {
                var ordered = repo == first ? entries.Order(StringComparer.Ordinal) : entries.OrderDescending(StringComparer.Ordinal);
                // Repeated observations must not produce repeated owner questions.
                return operation == "directories" ? ordered.Concat(ordered.Where(path =>
                    path == repo.PathFor("src/vanished") || path == repo.PathFor("src/also-missing"))) : ordered;
            };
            var adapter = new DotnetProjectDiscoveryAdapter(traversal);
            for (var repetition = 0; repetition < 2; repetition++)
            {
                traversal.ObservedDirectories.Clear();
                var model = adapter.Discover(repo.Root);
                AssertIncomplete(model, baseline, "src/also-missing", "src/vanished");
                snapshots.Add(ProjectModelJson.Serialize(model));
            }
        }

        Assert.NotEqual(first.Root, second.Root);
        Assert.All(snapshots, snapshot => Assert.Equal(snapshots[0], snapshot));
    }

    [Fact]
    public void RealLinksAndOutsideReferences_AreNotFollowed()
    {
        using var repo = new Repository();
        using var outside = new Repository();
        var directoryLink = repo.PathFor("linked");
        var fileLink = repo.PathFor("Linked.csproj");
        try
        {
            Directory.CreateSymbolicLink(directoryLink, outside.Root);
            File.CreateSymbolicLink(fileLink, outside.PathFor(ExpectedUnits[0]));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Symbolic-link precondition unavailable: {exception.Message}");
        }
        Assert.True((File.GetAttributes(directoryLink) & FileAttributes.ReparsePoint) != 0);
        Assert.True((File.GetAttributes(fileLink) & FileAttributes.ReparsePoint) != 0);
        var baseline = new DotnetProjectDiscoveryAdapter().Discover(repo.Root);
        Assert.Equal(ExpectedUnits, baseline.Units.Select(unit => unit.Id));
        Assert.Empty(DirectoryQuestions(baseline));

        var external = outside.PathFor(ExpectedUnits[0]).Replace('\\', '/');
        repo.Write("Sample.slnx", $"<Solution>\n<Project Path=\"linked/{ExpectedUnits[0]}\" />\n" +
            $"<Project Path=\"Linked.csproj\" />\n<Project Path=\"{external}\" />\n</Solution>");
        var model = new DotnetProjectDiscoveryAdapter().Discover(repo.Root);

        Assert.Equal(ExpectedUnits, model.Units.Select(unit => unit.Id));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"solutions/Sample.slnx/linked/{ExpectedUnits[0]}");
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "solutions/Sample.slnx/Linked.csproj");
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == $"solutions/Sample.slnx/{external}");
        Assert.Empty(DirectoryQuestions(model));
    }

    private static IEnumerable<ProjectOwnerQuestion> DirectoryQuestions(ProjectModel model) =>
        model.OwnerQuestions.Where(question => question.FactKey.StartsWith("discovery/directories/", StringComparison.Ordinal));

    private static void AssertIncomplete(ProjectModel model, ProjectModel baseline, params string[] missing)
    {
        Assert.Equal(ExpectedUnits, model.Units.Select(unit => unit.Id));
        var expected = missing.Select(path => new ProjectOwnerQuestion($"discovery/directories/{path}/membership",
            $"discovery/directories/{path}/membership: Discovery could not inspect directory '{path}'; project membership may be incomplete.",
            new FactSource(path, 1))).ToArray();
        Assert.Equal(expected, DirectoryQuestions(model));
        Assert.Equal(baseline.OwnerQuestions.Concat(expected).OrderBy(question => question.FactKey, StringComparer.Ordinal),
            model.OwnerQuestions);
        Assert.Equal(ProjectModelJson.Serialize(baseline),
            ProjectModelJson.Serialize(model with { OwnerQuestions = baseline.OwnerQuestions }));
    }

    private static Exception Missing(string type) => type == "FileNotFoundException"
        ? new FileNotFoundException("observed child disappeared") : new DirectoryNotFoundException("observed child disappeared");

    private static Traversal MissingAt(Repository repo, string relative, string operation, Exception failure, Action? beforeFailure = null)
    {
        var traversal = new Traversal();
        traversal.Before = (current, path) =>
        {
            if (current != operation || path != repo.PathFor(relative))
                return;
            Assert.Contains(path, traversal.ObservedDirectories);
            beforeFailure?.Invoke();
            throw failure;
        };
        return traversal;
    }

    private sealed class Traversal : DotnetProjectDiscoveryAdapter.IDirectoryTraversal
    {
        public HashSet<string> ObservedDirectories { get; } = new(StringComparer.Ordinal);
        public Action<string, string>? Before { get; set; }
        public Action<string, string>? After { get; init; }
        public Func<string, string, IEnumerable<string>, IEnumerable<string>>? Transform { get; set; }

        public FileAttributes GetAttributes(string path)
        {
            Before?.Invoke("attributes", path);
            var attributes = File.GetAttributes(path);
            After?.Invoke("attributes", path);
            return attributes;
        }

        public IEnumerable<string> EnumerateFiles(string directory) => Enumerate("files", directory);
        public IEnumerable<string> EnumerateDirectories(string directory) => Enumerate("directories", directory);

        private IEnumerable<string> Enumerate(string operation, string directory)
        {
            // Iterator callbacks run during materialization, not when the adapter requests an enumerable.
            Before?.Invoke(operation, directory);
            var entries = operation == "files" ? Directory.EnumerateFiles(directory) : Directory.EnumerateDirectories(directory);
            foreach (var entry in Transform?.Invoke(operation, directory, entries) ?? entries)
            {
                if (operation == "directories")
                    ObservedDirectories.Add(entry);
                yield return entry;
            }
            After?.Invoke(operation, directory);
        }
    }

    private sealed class Repository : IDisposable
    {
        private readonly string container = Path.Combine(Path.GetTempPath(), "disappearing-discovery", Guid.NewGuid().ToString("N"));
        public string Root { get; }

        public Repository()
        {
            Root = Path.Combine(container, "repo");
            foreach (var unit in ExpectedUnits)
                Write(unit, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Write("Sample.slnx", $"<Solution>\n<Project Path=\"{ExpectedUnits[0]}\" />\n<Project Path=\"{ExpectedUnits[1]}\" />\n</Solution>");
            Write("global.json", "{\"sdk\":{\"version\":\"10.0.100\"}}");
            ProjectOnboardingFixture.SeedRemoteDefault(Root);
        }

        public string PathFor(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public void Write(string relative, string contents)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void MoveRoot() => Directory.Move(Root, Path.Combine(container, "removed"));

        public void Dispose()
        {
            // Remove links themselves before recursive cleanup; outside fixtures own their targets.
            foreach (var path in new[] { PathFor("linked"), PathFor("Linked.csproj") })
            {
                if (Directory.Exists(path))
                    Directory.Delete(path);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            Directory.Delete(container, recursive: true);
        }
    }
}
