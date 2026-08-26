// Parallel-safe: each test injects an explicit unique root, and every Git child receives a
// hermetic environment. No verdict observes a shared temp root, process list, clock, or schedule.
public sealed class WorkerDispatchTestsSeededRepositoryFactoryTests
{
    [Xunit.Fact]
    public void Create_MultipleCopies_ProducesIndependentCommittedRepositories()
    {
        using var scope = new FactoryScope();

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

        Xunit.Assert.Equal(
            WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck.StagingHeadCommit,
            failure.Diagnostic.Check);
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

    private sealed class FactoryScope : IDisposable
    {
        private readonly Func<string, int, string>? _directoryAllocator;
        private int _nextDirectory;

        internal FactoryScope(
            WorkerDispatchTestsSeededRepositoryFactory.CreationHooks? hooks = null,
            Func<string, int, string>? directoryAllocator = null)
        {
            Root = InfrastructureTestSupport.CreateTempDirectory();
            _directoryAllocator = directoryAllocator;
            Factory = new WorkerDispatchTestsSeededRepositoryFactory(
                AllocateDirectory,
                path => File.WriteAllText(Path.Combine(path, "seed.txt"), "seed"),
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
    }

    private static string CreateOwnedDirectory(string root, int attempt)
    {
        var path = Path.Combine(root, $"factory-owned-{attempt}");
        Directory.CreateDirectory(path);
        return path;
    }
}
