// Parallel-safe: each test owns its staged repository, non-repository root, or parent repository with child root from SharedTestSupport.CreateTempDirectory().
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ProjectDiscoveryTrackedFilesTests
{
    [Fact]
    public void GoReplay_UntrackedCacheAndPackage_DoNotChangeModel()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            CopyTemplates(root, "go-module");
            StageRepository(root);
            var adapter = new GoProjectDiscoveryAdapter();
            var before = adapter.Discover(root);

            WriteGoNoise(root);
            // An untracked file inside an included directory must also be filtered.
            Write(root, "internal/util/extra.go", "package util\nimport \"C\"\n");
            var after = adapter.Discover(root);

            Assert.Equal(ProjectModelJson.Serialize(before), ProjectModelJson.Serialize(after));
            Assert.DoesNotContain(after.OwnerQuestions,
                question => question.FactKey.StartsWith("units/go-path/", StringComparison.Ordinal));
            Assert.Contains(before.OwnerQuestions, question => question.FactKey == "units/tools/module");
            Assert.Contains(after.OwnerQuestions, question => question.FactKey == "units/tools/module");
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void DotnetReplay_UntrackedProjectAndHazards_DoNotChangeModel()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            CopyTemplates(root, "solution");
            Write(root, "tests/Sample.MtpTests/Collections/Tracked.cs", Collection("Tracked"));
            StageRepository(root);
            var adapter = new DotnetProjectDiscoveryAdapter();
            var before = adapter.Discover(root);
            Assert.True(before.Units.Single(unit => unit.Id ==
                "tests/Sample.MtpTests/Sample.MtpTests.csproj").IsTest.Value);
            Assert.Contains(before.Hazards, hazard => hazard.IsolationKey.Value == "xunit:Tracked");

            WriteDotnetNoise(root);
            Write(root, "tests/Sample.MtpTests/Scratch/Collection.cs", Collection("UntrackedChild"));
            var after = adapter.Discover(root);

            Assert.Equal(ProjectModelJson.Serialize(before), ProjectModelJson.Serialize(after));
            Assert.DoesNotContain(after.Units, unit => unit.Id.StartsWith("go-path/", StringComparison.Ordinal));
            Assert.DoesNotContain(after.Hazards,
                hazard => hazard.IsolationKey.Value.Contains("Untracked", StringComparison.Ordinal));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void GoOutsideRepository_WalksCacheAndExtraPackage()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            CopyTemplates(root, "go-module");
            WriteGoNoise(root);

            var model = new GoProjectDiscoveryAdapter().Discover(root);

            Assert.Contains(model.OwnerQuestions, question => question.FactKey ==
                "units/go-path/pkg/mod/example.com/dep@v1.0.0/module");
            Assert.Contains(model.Units, unit => unit.Id == "scratch");
            Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == "discovery/tracked-files");
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void DotnetOutsideRepository_WalksExtraProjectAndHazard()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            CopyTemplates(root, "solution");
            WriteDotnetNoise(root);

            var model = new DotnetProjectDiscoveryAdapter().Discover(root);

            Assert.Contains(model.Units, unit => unit.Id == "go-path/Stray/Stray.csproj");
            Assert.Contains(model.Hazards, hazard => hazard.IsolationKey.Value == "xunit:Untracked");
            Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == "discovery/tracked-files");
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void DotnetBelowRepositoryTopLevel_KeepsFilesystemWalk()
    {
        var parent = SharedTestSupport.CreateTempDirectory();
        try
        {
            var root = Path.Combine(parent, "child");
            CopyTemplates(root, "solution");
            RunGit(parent, "init", "-q");
            RunGit(parent, "add", "-A");
            WriteDotnetNoise(root);

            var model = new DotnetProjectDiscoveryAdapter().Discover(root);

            Assert.Contains(model.Units, unit => unit.Id == "go-path/Stray/Stray.csproj");
            Assert.Contains(model.Hazards, hazard => hazard.IsolationKey.Value == "xunit:Untracked");
            Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == "discovery/tracked-files");
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(parent);
        }
    }

    [Theory]
    [InlineData(128, true, false)]
    [InlineData(-1, false, false)]
    [InlineData(0, true, true)]
    public void Read_FailedTopLevel_WalksWithoutDegradation(int exitCode, bool started, bool drainTimedOut)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var calls = new List<string[]>();
            var tracked = DiscoveryTrackedPaths.Read(root, (directory, arguments) =>
            {
                Assert.Equal(root, directory);
                calls.Add(arguments);
                return new GitCli.GitResult(exitCode, root + "\n", "not a repository", drainTimedOut, started);
            });
            var questions = new List<ProjectOwnerQuestion>();

            Assert.True(tracked.IncludesFile(Path.Combine(root, "arbitrary.cs")));
            Assert.True(tracked.IncludesDirectory(Path.Combine(root, "arbitrary")));
            tracked.AskIfDegraded(questions);
            Assert.Empty(questions);
            Assert.Equal(new[] { "rev-parse", "--show-toplevel" }, Assert.Single(calls));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void Read_MatchingTopLevel_UsesExactCommandsAndTrackedAncestors()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var calls = new List<string[]>();
            var tracked = DiscoveryTrackedPaths.Read(root, (directory, arguments) =>
            {
                Assert.Equal(root, directory);
                calls.Add(arguments);
                return new GitCli.GitResult(0, calls.Count == 1
                    ? root.Replace('\\', '/') + "/\n"
                    : "src/nested/Tracked.cs\0 spaced / file with spaces.cs\0line\nbreak.cs\0", "");
            });

            Assert.True(tracked.IncludesDirectory(root));
            Assert.True(tracked.IncludesDirectory(Path.Combine(root, "src")));
            Assert.True(tracked.IncludesDirectory(Path.Combine(root, "src/nested")));
            Assert.True(tracked.IncludesFile(Path.Combine(root, "src/nested/Tracked.cs")));
            Assert.True(tracked.IncludesFile(Path.Combine(root, " spaced / file with spaces.cs")));
            Assert.True(tracked.IncludesFile(Path.Combine(root, "line\nbreak.cs")));
            Assert.False(tracked.IncludesFile(Path.Combine(root, "src/nested/Unlisted.cs")));
            Assert.False(tracked.IncludesDirectory(Path.Combine(root, "unlisted")));
            Assert.False(tracked.IncludesFile(Path.Combine(root, "../outside.cs")));
            var questions = new List<ProjectOwnerQuestion>();
            tracked.AskIfDegraded(questions);
            Assert.Empty(questions);
            Assert.Collection(calls,
                arguments => Assert.Equal(new[] { "rev-parse", "--show-toplevel" }, arguments),
                arguments => Assert.Equal(new[] { "ls-files", "-z", "--cached", "--recurse-submodules" }, arguments));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Theory]
    [InlineData(128, true, false, "first error\nsecond error", "first error")]
    [InlineData(-1, false, false, "start failure\r\nsecond error", "start failure")]
    [InlineData(0, true, true, "", "stderr was empty")]
    public void Read_ListingFailure_WalksAndAsksOnce(
        int exitCode, bool started, bool drainTimedOut, string error, string firstError)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var tracked = DiscoveryTrackedPaths.Read(root, (_, arguments) =>
                arguments[0] == "rev-parse" ? new GitCli.GitResult(0, root + "\n", "")
                    : new GitCli.GitResult(exitCode, "partial.cs\0", error, drainTimedOut, started));
            var questions = new List<ProjectOwnerQuestion>();

            Assert.True(tracked.IncludesFile(Path.Combine(root, "arbitrary.cs")));
            Assert.True(tracked.IncludesDirectory(Path.Combine(root, "arbitrary")));
            tracked.AskIfDegraded(questions);
            var question = Assert.Single(questions);
            Assert.Equal("discovery/tracked-files", question.FactKey);
            Assert.Contains("Tracked files could not be listed", question.Question, StringComparison.Ordinal);
            Assert.Contains("untracked directories were walked", question.Question, StringComparison.Ordinal);
            Assert.Contains($"exit {exitCode}", question.Question, StringComparison.Ordinal);
            Assert.Contains(firstError, question.Question, StringComparison.Ordinal);
            Assert.DoesNotContain("second error", question.Question, StringComparison.Ordinal);
            if (!started)
                Assert.Contains("process did not start", question.Question, StringComparison.Ordinal);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void Read_EmptyTrackedInventory_IncludesOnlyRootDirectory()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var tracked = DiscoveryTrackedPaths.Read(root, (_, arguments) =>
                new GitCli.GitResult(0, arguments[0] == "rev-parse" ? root + "\n" : "", ""));

            Assert.True(tracked.IncludesDirectory(root));
            Assert.False(tracked.IncludesFile(Path.Combine(root, "arbitrary.cs")));
            Assert.False(tracked.IncludesDirectory(Path.Combine(root, "arbitrary")));
            var questions = new List<ProjectOwnerQuestion>();
            tracked.AskIfDegraded(questions);
            Assert.Empty(questions);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    [InlineData("child")]
    public void Read_UnusableOrDifferentTopLevel_WalksWithoutDegradation(string suffix)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var calls = 0;
            var tracked = DiscoveryTrackedPaths.Read(root, (_, _) =>
            {
                calls++;
                return new GitCli.GitResult(0, suffix.Length == 0 ? "" : Path.Combine(root, suffix) + "\n", "");
            });

            Assert.Equal(1, calls);
            Assert.True(tracked.IncludesFile(Path.Combine(root, "arbitrary.cs")));
            var questions = new List<ProjectOwnerQuestion>();
            tracked.AskIfDegraded(questions);
            Assert.Empty(questions);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void CopyTemplates(string root, string name)
    {
        var source = Path.Combine(VerifiedRepositoryRoot.Find(), "tests", "project-onboarding-fixture", name);
        foreach (var template in Directory.EnumerateFiles(source, "*.template", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(root, Path.GetRelativePath(source, template)[..^".template".Length]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(template, destination);
        }
    }

    private static void StageRepository(string root)
    {
        RunGit(root, "init", "-q");
        RunGit(root, "add", "-A");
    }

    private static void RunGit(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, arguments);
        Assert.True(result.ProcessStarted && result.Succeeded && !result.DrainTimedOut,
            $"git {string.Join(' ', arguments)} failed: exit={result.ExitCode}; stderr={result.Error}");
    }

    private static void WriteGoNoise(string root)
    {
        Write(root, "go-path/pkg/mod/example.com/dep@v1.0.0/go.mod", "module example.com/dep\n");
        Write(root, "go-path/pkg/mod/example.com/dep@v1.0.0/dep.go", "package dep\n");
        Write(root, "scratch/extra.go", "package scratch\n");
    }

    private static void WriteDotnetNoise(string root)
    {
        Write(root, "go-path/Stray/Stray.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(root, "tests/Sample.MtpTests/UntrackedCollection.cs", Collection("Untracked"));
    }

    private static string Collection(string name) =>
        $"[CollectionDefinition(\"{name}\", DisableParallelization = true)] public sealed class Serial;";

    private static void Write(string root, string path, string text)
    {
        var destination = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, text);
    }
}
