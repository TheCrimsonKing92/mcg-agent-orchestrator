using System.Text;
using Factory = WorkerDispatchTestsSeededRepositoryFactory;
using Check = WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck;
using Classification = WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification;

public sealed class WorkerDispatchTestsSeededRepositoryFactoryLaunchCount
{
    [Xunit.Fact]
    public void Create_UnchangedTemplate_UsesSeventeenThenSixLaunches()
    {
        using var scope = new FactoryScope();
        scope.Factory.Create();
        Xunit.Assert.Equal(17, scope.Runner.Count);
        for (var create = 0; create < 2; create++)
        {
            var before = scope.Runner.Count;
            scope.Factory.Create();
            Xunit.Assert.Equal(6, scope.Runner.Count - before);
        }
    }

    [Xunit.Fact]
    public void Create_CachedTemplateHeadRemoved_RejectsWithOneLaunch()
    {
        using var scope = new FactoryScope();
        var template = scope.Factory.Create().TemplateIdentity.RepositoryPath;
        File.Delete(Path.Combine(template, ".git", "HEAD"));
        AssertTemplateFailure(scope, Check.TemplateHeadCommit, 1);
    }

    [Xunit.Fact]
    public void Create_CachedTemplatePayloadChanged_RejectsWithFiveLaunches()
    {
        using var scope = new FactoryScope();
        var template = scope.Factory.Create().TemplateIdentity.RepositoryPath;
        File.WriteAllText(Path.Combine(template, "seed.txt"), "changed seed content");
        AssertTemplateFailure(scope, Check.TemplateStatus, 5);
    }

    [Xunit.Fact]
    public void Create_CachedTemplateMetadataRemoved_RejectsWithoutLaunches()
    {
        using var scope = new FactoryScope();
        var template = scope.Factory.Create().TemplateIdentity.RepositoryPath;
        Factory.DeleteOwnedDirectory(Path.Combine(template, ".git"));
        AssertTemplateFailure(scope, Check.TemplateMetadata, 0);
    }

    [Xunit.Fact]
    public void Create_CachedTemplateHeadMoved_RejectsWithFiveLaunches()
    {
        using var scope = new FactoryScope();
        var template = scope.Factory.Create().TemplateIdentity.RepositoryPath;
        File.WriteAllText(Path.Combine(template, "seed.txt"), "committed change");
        var commitTime = DateTimeOffset.Parse("2026-01-02T00:00:00Z");
        Factory.RunFixtureGit(template, ["add", "-A"], commitTime);
        Factory.RunFixtureGit(template, ["commit", "-m", "Change template"], commitTime);
        AssertTemplateFailure(scope, Check.TemplateIdentityChanged, 5);
    }

    [Xunit.Fact]
    public void Create_CachedHeadBytesChangedWithSameLengthAndTimestamp_RejectsWithOneLaunch()
    {
        using var scope = new FactoryScope();
        var template = scope.Factory.Create().TemplateIdentity.RepositoryPath;
        var headPath = Path.Combine(template, ".git", "HEAD");
        var head = File.ReadAllBytes(headPath);
        var timestamp = File.GetLastWriteTimeUtc(headPath);
        File.WriteAllBytes(headPath, Enumerable.Repeat((byte)'!', head.Length).ToArray());
        File.SetLastWriteTimeUtc(headPath, timestamp);
        AssertTemplateFailure(scope, Check.TemplateHeadCommit, 1);
    }

    [Xunit.Fact]
    public void Create_CachedTemplateHeadRemovedAfterCopy_RejectsBeforeStagingProbe()
    {
        var removeHead = false;
        using var scope = new FactoryScope(new Factory.CreationHooks(
            AfterCopy: (template, _) =>
            {
                if (removeHead)
                {
                    File.Delete(Path.Combine(template, ".git", "HEAD"));
                }
            }));
        scope.Factory.Create();
        removeHead = true;
        AssertTemplateFailure(scope, Check.TemplateAfterCopyHeadCommit, 1);
    }

    [Xunit.Fact]
    public void Create_CollapsedStagingProbe_RetainsChecksAndClassifications() =>
        AssertCollapsedFailures(published: false);

    [Xunit.Fact]
    public void Create_CollapsedPublishedProbe_RetainsChecksAndClassifications() =>
        AssertCollapsedFailures(published: true);

    [Xunit.Fact]
    public void Create_UntrackedStagingFile_RejectsStatus()
    {
        using var scope = new FactoryScope(new Factory.CreationHooks(
            AfterCopy: (_, staging) => File.WriteAllText(Path.Combine(staging, "untracked.txt"), "untracked")));
        var failure = Xunit.Assert.Throws<Factory.SeededRepositoryFailureException>(() => scope.Factory.Create());
        Xunit.Assert.Equal(Check.StagingStatus, failure.Diagnostic.Check);
    }

    [Xunit.Fact]
    public async Task Create_ConcurrentCachedCopies_UseTwelveLaunches()
    {
        using var scope = new FactoryScope();
        scope.Factory.Create();
        var before = scope.Runner.Count;
        var copies = await Task.WhenAll(
            Task.Run(() => scope.Factory.Create()), Task.Run(() => scope.Factory.Create()));
        Xunit.Assert.Equal(2, copies.Length);
        Xunit.Assert.NotEqual(copies[0].PublishedIdentity.RepositoryPath, copies[1].PublishedIdentity.RepositoryPath);
        Xunit.Assert.All(copies, copy => Xunit.Assert.True(Directory.Exists(copy.PublishedIdentity.RepositoryPath)));
        Xunit.Assert.Equal(12, scope.Runner.Count - before);
    }

    private static void AssertTemplateFailure(FactoryScope scope, Check check, int expectedLaunches)
    {
        var before = scope.Runner.Count;
        var failure = Xunit.Assert.Throws<Factory.SeededRepositoryFailureException>(() => scope.Factory.Create());
        Xunit.Assert.Equal(check, failure.Diagnostic.Check);
        Xunit.Assert.Equal(expectedLaunches, scope.Runner.Count - before);
    }

    private static void AssertCollapsedFailures(bool published)
    {
        var scenarios = new (string Output, string Suffix, Classification Classification)[]
        {
            ("false\n{path}\n.git\n", "InsideWorkTree", Classification.InvalidRequiredOutput),
            ("true\n{template}\n.git\n", "TopLevel", Classification.InvalidRequiredOutput),
            ("true\n{path}\n{template}/.git\n", "GitDirectory", Classification.InvalidRequiredOutput),
            ("", "TopLevel", Classification.EmptyRequiredOutput),
            ("\n{path}\n.git\n", "InsideWorkTree", Classification.InvalidRequiredOutput),
            ("true\n\n.git\n", "TopLevel", Classification.InvalidRequiredOutput),
            ("true\n{path}\n\n", "GitDirectory", Classification.InvalidRequiredOutput),
            ("true\n   \n.git\n", "TopLevel", Classification.InvalidRequiredOutput),
            ("true\n{path}\n.git\nunexpected\n", "GitDirectory", Classification.InvalidRequiredOutput)
        };
        foreach (var scenario in scenarios)
        {
            using var scope = new FactoryScope();
            var rewrites = 0;
            scope.Runner.Rewrite = (path, arguments, result) =>
            {
                if (!arguments.Contains("--show-toplevel") || path == scope.TemplatePath ||
                    published == scope.IsStaging(path))
                {
                    return result;
                }
                Interlocked.Increment(ref rewrites);
                var output = scenario.Output.Replace("{path}", path, StringComparison.Ordinal)
                    .Replace("{template}", scope.TemplatePath!, StringComparison.Ordinal);
                return result with
                {
                    StandardOutput = output,
                    StandardOutputByteCount = Encoding.UTF8.GetByteCount(output)
                };
            };

            var failure = Xunit.Assert.Throws<Factory.SeededRepositoryFailureException>(() => scope.Factory.Create());
            var check = Enum.Parse<Check>((published ? "Published" : "Staging") + scenario.Suffix);
            Xunit.Assert.Equal(1, rewrites);
            Xunit.Assert.Equal(check, failure.Diagnostic.Check);
            Xunit.Assert.Equal(scenario.Classification, failure.Diagnostic.Git.Classification);
            Xunit.Assert.Contains(failure.Diagnostic.ProbeReceipts!, receipt =>
                receipt.Check == check && receipt.Classification == scenario.Classification &&
                receipt.Arguments!.TakeLast(4).SequenceEqual(["rev-parse", "--is-inside-work-tree", "--show-toplevel", "--git-dir"]));
        }
    }

    private sealed class CountingGitRunner : Factory.IGitRunner
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        internal Func<string, IReadOnlyList<string>, Factory.GitProbeResult, Factory.GitProbeResult>? Rewrite { get; set; }

        public Factory.GitProbeResult Run(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? commandEnvironment = null)
        {
            Interlocked.Increment(ref _count);
            var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments, commandEnvironment);
            return Rewrite?.Invoke(workingDirectory, arguments, result) ?? result;
        }
    }

    // Every fact owns its root, allocator and runner; no collection or shared state is needed.
    private sealed class FactoryScope : IDisposable
    {
        private int _nextDirectory;
        private readonly string _root;
        internal FactoryScope(Factory.CreationHooks? hooks = null)
        {
            var processTempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            var parent = AssemblyTempRedirect.TryParseProcessTempRootName(Path.GetFileName(processTempRoot), out _)
                ? Path.GetDirectoryName(processTempRoot)
                    ?? throw new InvalidOperationException($"The process temp root has no parent: {processTempRoot}")
                : processTempRoot;
            _root = Path.Combine(parent, $"factory-launch-count-{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            Factory = new Factory(AllocateDirectory,
                path => File.WriteAllText(Path.Combine(path, "seed.txt"), "seed"), _root,
                gitRunner: Runner, hooks: hooks);
        }

        internal Factory Factory { get; }
        internal CountingGitRunner Runner { get; } = new();
        internal string? TemplatePath { get; private set; }
        internal bool IsStaging(string path) => Path.GetFileName(path).StartsWith("factory-owned-", StringComparison.Ordinal);

        private string AllocateDirectory()
        {
            var allocation = Interlocked.Increment(ref _nextDirectory);
            var path = Path.Combine(_root, $"factory-owned-{allocation}");
            Directory.CreateDirectory(path);
            if (allocation == 1)
            {
                TemplatePath = path;
            }
            return path;
        }

        public void Dispose() => WorkerDispatchTestsSeededRepositoryFactory.DeleteOwnedDirectory(_root);
    }
}
