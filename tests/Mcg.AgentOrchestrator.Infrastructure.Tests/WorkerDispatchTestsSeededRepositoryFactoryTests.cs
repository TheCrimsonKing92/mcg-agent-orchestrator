// Parallel-safe: each test injects an explicit unique root, and every Git child receives a
// hermetic environment. No verdict observes a shared temp root, process list, clock, or schedule.
public sealed class WorkerDispatchTestsSeededRepositoryFactoryTests
{
    [Xunit.Fact]
    public void Create_MultipleCopies_ProducesIndependentCommittedRepositories()
    {
        using var scope = new FactoryScope(directoryAllocator: static (root, attempt) =>
        {
            var path = Path.Combine(root, attempt.ToString("D32"));
            Directory.CreateDirectory(path);
            return path;
        });

        var first = scope.Factory.Create();
        var second = scope.Factory.Create();

        Xunit.Assert.NotEqual(
            first.PublishedIdentity.RepositoryPath,
            second.PublishedIdentity.RepositoryPath);
        Xunit.Assert.NotEqual(
            first.PublishedIdentity.GitDirectoryPath,
            second.PublishedIdentity.GitDirectoryPath);
        Xunit.Assert.Equal(
            first.TemplateIdentity.HeadCommit,
            first.PublishedIdentity.HeadCommit);
        Xunit.Assert.Equal(
            second.TemplateIdentity.HeadCommit,
            second.PublishedIdentity.HeadCommit);
        Xunit.Assert.Equal(
            first.PublishedIdentity.RepositoryPath,
            first.PublishedIdentity.TopLevelPath);
        Xunit.Assert.Equal(
            second.PublishedIdentity.RepositoryPath,
            second.PublishedIdentity.TopLevelPath);
        Xunit.Assert.True(Directory.Exists(first.PublishedIdentity.GitDirectoryPath));
        Xunit.Assert.True(Directory.Exists(second.PublishedIdentity.GitDirectoryPath));
        Xunit.Assert.Equal(32, Path.GetFileName(first.PublishedIdentity.RepositoryPath).Length);
        Xunit.Assert.Equal(32, Path.GetFileName(second.PublishedIdentity.RepositoryPath).Length);
    }

    [Xunit.Fact]
    public void Create_MissingTemplateMetadata_ReportsTemplateCheck()
    {
        string? invalidatedTemplate = null;
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            BeforeTemplateValidation: path =>
            {
                invalidatedTemplate = path;
                WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(Path.Combine(path, ".git"));
            }));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateMetadata,
            failure.Diagnostic.Check);
        Xunit.Assert.Equal(invalidatedTemplate, failure.Diagnostic.SourceTemplatePath);
        Xunit.Assert.False(failure.Diagnostic.FileSystem.GitMetadataDirectoryExists);
        Xunit.Assert.False(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.SourceTemplatePath));
    }

    [Xunit.Fact]
    public void Create_CorruptCopiedHead_ReportsDestinationCheck()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterCopy: (_, staging) => File.Delete(Path.Combine(staging, ".git", "HEAD"))));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.True(
            failure.Diagnostic.Check ==
                WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingHeadCommit,
            failure.Message);
        Xunit.Assert.NotNull(failure.Diagnostic.TemplateIdentity);
        Xunit.Assert.NotNull(failure.Diagnostic.StagingPath);
        Xunit.Assert.NotNull(failure.Diagnostic.FinalPath);
        Xunit.Assert.False(failure.Diagnostic.FileSystem.HeadFileExists);
        Xunit.Assert.True(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.NotEqual(0, failure.Diagnostic.Git.ExitCode);
        Xunit.Assert.Contains("HEAD^{commit}", failure.Diagnostic.Git.Command, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "repositorySelection=unset",
            failure.Diagnostic.Git.EnvironmentContract,
            StringComparison.Ordinal);
        Xunit.Assert.False(failure.Diagnostic.Git.DrainTimedOut);
        Xunit.Assert.False(failure.Diagnostic.Git.TimedOut);
        Xunit.Assert.False(failure.Diagnostic.Git.DrainFailed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("timeout")]
    [Xunit.InlineData("drain-timeout")]
    [Xunit.InlineData("drain-failure")]
    [Xunit.InlineData("malformed-output")]
    public void Create_GitProbeDecisionTable_ReportsTypedTemplateHeadCheck(string scenario)
    {
        var runner = new InterceptingGitRunner(result => scenario switch
        {
            "timeout" => result with { ExitCode = null, TimedOut = true },
            "drain-timeout" => result with { DrainTimedOut = true },
            "drain-failure" => result with { DrainFailed = true, StandardError = "controlled drain failure" },
            "malformed-output" => result with { StandardOutput = "not-a-commit\n" },
            _ => throw new InvalidOperationException($"Unknown scenario: {scenario}")
        });
        using var scope = new FactoryScope(gitRunner: runner);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplateHeadCommit,
            failure.Diagnostic.Check);
        Xunit.Assert.True(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.Contains("HEAD^{commit}", failure.Diagnostic.Git.Command, StringComparison.Ordinal);
        switch (scenario)
        {
            case "timeout":
                Xunit.Assert.True(failure.Diagnostic.Git.TimedOut);
                Xunit.Assert.Null(failure.Diagnostic.Git.ExitCode);
                break;
            case "drain-timeout":
                Xunit.Assert.True(failure.Diagnostic.Git.DrainTimedOut);
                break;
            case "drain-failure":
                Xunit.Assert.True(failure.Diagnostic.Git.DrainFailed);
                Xunit.Assert.Contains("controlled drain failure", failure.Diagnostic.Git.StandardError);
                break;
            case "malformed-output":
                Xunit.Assert.Equal(0, failure.Diagnostic.Git.ExitCode);
                Xunit.Assert.Equal("not-a-commit\n", failure.Diagnostic.Git.StandardOutput);
                break;
        }
    }

    [Xunit.Fact]
    public void RunGitProbe_InjectedAmbientSelection_IsRemovedAndOptionalLocksAreDisabled()
    {
        using var scope = new FactoryScope();
        IReadOnlyDictionary<string, string?>? capturedEnvironment = null;
        var inheritedEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "controlled-path",
            ["GIT_DIR"] = "redirected-git-dir",
            ["GIT_WORK_TREE"] = "redirected-work-tree",
            ["GIT_INDEX_FILE"] = "redirected-index"
        };

        var result = InfrastructureTestSupport.RunGitProbe(
            scope.Root,
            ["status", "--porcelain=v1"],
            commandEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GIT_DIR"] = "command-redirected-git-dir",
                ["GIT_OPTIONAL_LOCKS"] = "1"
            },
            startProcess: process =>
            {
                capturedEnvironment = process.StartInfo.Environment.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
                return false;
            },
            inheritedEnvironment: inheritedEnvironment);

        Xunit.Assert.False(result.ProcessStarted);
        Xunit.Assert.NotNull(capturedEnvironment);
        Xunit.Assert.DoesNotContain("GIT_DIR", capturedEnvironment.Keys);
        Xunit.Assert.DoesNotContain("GIT_WORK_TREE", capturedEnvironment.Keys);
        Xunit.Assert.DoesNotContain("GIT_INDEX_FILE", capturedEnvironment.Keys);
        Xunit.Assert.Equal("0", capturedEnvironment["GIT_OPTIONAL_LOCKS"]);
    }

    [Xunit.Fact]
    public void Create_FailedValidation_CleansOnlyAttemptOwnedPaths()
    {
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterPublish: published => File.Delete(Path.Combine(published, ".git", "HEAD"))));
        var unrelated = Path.Combine(scope.Root, "unrelated-sibling");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.PublishedHeadCommit,
            failure.Diagnostic.Check);
        Xunit.Assert.False(failure.Diagnostic.FileSystem.HeadFileExists);
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.StagingPath!));
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.FinalPath!));
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.SourceTemplatePath));
        Xunit.Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
    }

    [Xunit.Fact]
    public void Create_CleanupFailure_IsRetainedInTypedDiagnostic()
    {
        string? publishedPath = null;
        var fileSystem = new TestFileSystem(
            path => string.Equals(path, publishedPath, StringComparison.Ordinal));
        using var scope = new FactoryScope(
            hooks: new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
                AfterPublish: published =>
                {
                    publishedPath = published;
                    File.Delete(Path.Combine(published, ".git", "HEAD"));
                }),
            fileSystem: fileSystem);

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        var publishedCleanup = Xunit.Assert.Single(
            failure.Diagnostic.Cleanup,
            outcome => string.Equals(outcome.Path, failure.Diagnostic.FinalPath, StringComparison.Ordinal));
        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.CleanupDisposition.Failed,
            publishedCleanup.Disposition);
        Xunit.Assert.Contains("controlled cleanup failure", publishedCleanup.Error, StringComparison.Ordinal);
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.FinalPath));
        Xunit.Assert.Contains("cleanup=(", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Create_SuccessfulCopy_SurvivesSiblingFailureCleanup()
    {
        var copyCount = 0;
        using var scope = new FactoryScope(new WorkerDispatchTestsSeededRepositoryFactory.CreationHooks(
            AfterCopy: (_, staging) =>
            {
                if (Interlocked.Increment(ref copyCount) == 2)
                {
                    File.Delete(Path.Combine(staging, ".git", "HEAD"));
                }
            }));
        var successful = scope.Factory.Create();

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingHeadCommit,
            failure.Diagnostic.Check);
        Xunit.Assert.True(Directory.Exists(successful.PublishedIdentity.RepositoryPath));
        Xunit.Assert.True(File.Exists(Path.Combine(successful.PublishedIdentity.GitDirectoryPath, "HEAD")));
        Xunit.Assert.True(File.Exists(Path.Combine(successful.TemplateIdentity.GitDirectoryPath, "HEAD")));
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.StagingPath!));
        Xunit.Assert.False(Directory.Exists(failure.Diagnostic.FinalPath!));
    }

    [Xunit.Fact]
    public void RunGitProbe_StartThrows_ReturnsTypedLaunchFailure()
    {
        using var scope = new FactoryScope();

        var result = InfrastructureTestSupport.RunGitProbe(
            scope.Root,
            ["status", "--short"],
            startProcess: _ => throw new System.ComponentModel.Win32Exception("controlled launch failure"));

        Xunit.Assert.False(result.ProcessStarted);
        Xunit.Assert.Null(result.ExitCode);
        Xunit.Assert.Equal(string.Empty, result.StandardOutput);
        Xunit.Assert.Contains("controlled launch failure", result.StandardError, StringComparison.Ordinal);
        Xunit.Assert.False(result.DrainTimedOut);
        Xunit.Assert.False(result.TimedOut);
        Xunit.Assert.False(result.DrainFailed);
        Xunit.Assert.Contains("git status --short", result.Command, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Create_StagingAllocationThrows_ReportsTypedCheck()
    {
        using var scope = new FactoryScope(
            directoryAllocator: (root, attempt) => attempt == 2
                ? throw new IOException("controlled staging allocation failure")
                : CreateOwnedDirectory(root, attempt));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingPathAllocated,
            failure.Diagnostic.Check);
        Xunit.Assert.NotNull(failure.Diagnostic.SourceTemplatePath);
        Xunit.Assert.NotNull(failure.Diagnostic.TemplateIdentity);
        Xunit.Assert.Null(failure.Diagnostic.StagingPath);
        Xunit.Assert.Null(failure.Diagnostic.FinalPath);
        Xunit.Assert.False(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.Contains(
            "controlled staging allocation failure",
            failure.Diagnostic.Git.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.True(Directory.Exists(failure.Diagnostic.SourceTemplatePath));
    }

    [Xunit.Fact]
    public void Create_TemplateAllocationThrows_ReportsTypedCheck()
    {
        using var scope = new FactoryScope(
            directoryAllocator: (_, _) => throw new IOException("controlled template allocation failure"));

        var failure = Xunit.Assert.Throws<
            WorkerDispatchTestsSeededRepositoryFactory.SeededRepositoryFailureException>(
                () => scope.Factory.Create());

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.TemplatePathAllocated,
            failure.Diagnostic.Check);
        Xunit.Assert.Null(failure.Diagnostic.SourceTemplatePath);
        Xunit.Assert.Null(failure.Diagnostic.TemplateIdentity);
        Xunit.Assert.Null(failure.Diagnostic.StagingPath);
        Xunit.Assert.Null(failure.Diagnostic.FinalPath);
        Xunit.Assert.False(failure.Diagnostic.Git.ProcessStarted);
        Xunit.Assert.Contains(
            "controlled template allocation failure",
            failure.Diagnostic.Git.StandardError,
            StringComparison.Ordinal);
    }

    private sealed class InterceptingGitRunner(
        Func<WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult,
            WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult> transform)
        : WorkerDispatchTestsSeededRepositoryFactory.IGitRunner
    {
        private int _intercepted;

        public WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult Run(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? commandEnvironment = null)
        {
            var result = InfrastructureTestSupport.RunGitProbe(
                workingDirectory,
                arguments,
                commandEnvironment);
            if (result.Succeeded &&
                arguments.SequenceEqual(["rev-parse", "--verify", "HEAD^{commit}"]) &&
                Interlocked.CompareExchange(ref _intercepted, 1, 0) == 0)
            {
                return transform(result);
            }

            return result;
        }
    }

    private sealed class TestFileSystem(Func<string, bool> failDelete)
        : WorkerDispatchTestsSeededRepositoryFactory.IFileSystem
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);

        public WorkerDispatchTestsSeededRepositoryFactory.FileSystemObservation ObserveRepository(string path) =>
            new(
                Directory.Exists(path),
                Directory.Exists(Path.Combine(path, ".git")),
                File.Exists(Path.Combine(path, ".git")),
                File.Exists(Path.Combine(path, ".git", "HEAD")));

        public void CopyDirectoryContents(string source, string destination)
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        public void MoveDirectory(string source, string destination) =>
            Directory.Move(source, destination);

        public void DeleteDirectory(string path)
        {
            if (failDelete(path))
            {
                throw new IOException("controlled cleanup failure");
            }

            WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(path);
        }
    }

    private sealed class FactoryScope : IDisposable
    {
        private readonly Func<string, int, string>? _directoryAllocator;
        private int _nextDirectory;

        internal FactoryScope(
            WorkerDispatchTestsSeededRepositoryFactory.CreationHooks? hooks = null,
            Func<string, int, string>? directoryAllocator = null,
            WorkerDispatchTestsSeededRepositoryFactory.IFileSystem? fileSystem = null,
            WorkerDispatchTestsSeededRepositoryFactory.IGitRunner? gitRunner = null)
        {
            Root = CreateIsolatedFactoryRoot();
            _directoryAllocator = directoryAllocator;
            Factory = new WorkerDispatchTestsSeededRepositoryFactory(
                AllocateDirectory,
                path => File.WriteAllText(Path.Combine(path, "seed.txt"), "seed"),
                fileSystem,
                gitRunner,
                hooks: hooks);
        }

        internal string Root { get; }

        internal WorkerDispatchTestsSeededRepositoryFactory Factory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(Root);
            }
        }

        private string AllocateDirectory()
        {
            var attempt = Interlocked.Increment(ref _nextDirectory);
            return _directoryAllocator?.Invoke(Root, attempt) ?? CreateOwnedDirectory(Root, attempt);
        }

        private static string CreateIsolatedFactoryRoot()
        {
            var processTempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            var rootParent = AssemblyTempRedirect.TryParseProcessTempRootName(
                Path.GetFileName(processTempRoot),
                out _)
                    ? Path.GetDirectoryName(processTempRoot)
                        ?? throw new InvalidOperationException(
                            $"The process temp root has no parent: {processTempRoot}")
                    : processTempRoot;
            var root = Path.Combine(
                rootParent,
                $"factory-{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    private static string CreateOwnedDirectory(string root, int attempt)
    {
        var path = Path.Combine(root, $"factory-owned-{attempt}");
        Directory.CreateDirectory(path);
        return path;
    }
}
