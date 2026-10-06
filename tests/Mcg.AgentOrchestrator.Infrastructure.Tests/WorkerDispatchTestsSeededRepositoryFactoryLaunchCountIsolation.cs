using System.Text;
using Factory = WorkerDispatchTestsSeededRepositoryFactory;
using Check = WorkerDispatchTestsSeededRepositoryFactory.ValidationCheck;
using Classification = WorkerDispatchTestsSeededRepositoryFactory.GitProbeClassification;

public sealed class WorkerDispatchTestsSeededRepositoryFactoryLaunchCountIsolation
{
    [Xunit.Fact]
    public void Create_AfterSeparateFactorySuccess_RetainsCollapsedPublishedFailures()
    {
        using var warmup = new FactoryScope();
        var result = warmup.Factory.Create();
        Xunit.Assert.True(Directory.Exists(result.PublishedIdentity.RepositoryPath));
        Xunit.Assert.Equal(16, warmup.Runner.Count);

        AssertCollapsedPublishedFailures();
    }

    [Xunit.Fact]
    public void Create_UnchangedThenChangedTemplate_RetainsLaunchCounts()
    {
        AssertUnchangedTemplateLaunchCounts();
        AssertChangedTemplateLaunchCounts();
    }

    [Xunit.Fact]
    public void Create_ChangedThenUnchangedTemplate_RetainsLaunchCounts()
    {
        AssertChangedTemplateLaunchCounts();
        AssertUnchangedTemplateLaunchCounts();
    }

    private static void AssertUnchangedTemplateLaunchCounts()
    {
        using var scope = new FactoryScope();
        scope.Factory.Create();
        Xunit.Assert.Equal(16, scope.Runner.Count);

        var before = scope.Runner.Count;
        scope.Factory.Create();
        Xunit.Assert.Equal(6, scope.Runner.Count - before);

        before = scope.Runner.Count;
        scope.Factory.Create();
        Xunit.Assert.Equal(6, scope.Runner.Count - before);
    }

    private static void AssertChangedTemplateLaunchCounts()
    {
        using var scope = new FactoryScope();
        var template = scope.Factory.Create().TemplateIdentity.RepositoryPath;
        File.WriteAllText(Path.Combine(template, "seed.txt"), "changed seed content");
        var before = scope.Runner.Count;
        var failure = Xunit.Assert.Throws<Factory.SeededRepositoryFailureException>(() => scope.Factory.Create());
        Xunit.Assert.Equal(Check.TemplateStatus, failure.Diagnostic.Check);
        Xunit.Assert.Equal(5, scope.Runner.Count - before);
    }

    private static void AssertCollapsedPublishedFailures()
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
                if (!arguments.Contains("--show-toplevel") || path == scope.TemplatePath || scope.IsStaging(path))
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
            var check = Enum.Parse<Check>("Published" + scenario.Suffix);
            Xunit.Assert.Equal(check, failure.Diagnostic.Check);
            Xunit.Assert.Equal(scenario.Classification, failure.Diagnostic.Git.Classification);
            Xunit.Assert.Equal(1, rewrites);
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

    // Every fact owns its roots, allocators and runners; no collection or shared state is needed.
    private sealed class FactoryScope : IDisposable
    {
        private int _nextDirectory;
        private readonly string _root;
        internal FactoryScope()
        {
            var processTempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            var parent = AssemblyTempRedirect.TryParseProcessTempRootName(Path.GetFileName(processTempRoot), out _)
                ? Path.GetDirectoryName(processTempRoot)
                    ?? throw new InvalidOperationException($"The process temp root has no parent: {processTempRoot}")
                : processTempRoot;
            _root = Path.Combine(parent, $"factory-launch-isolation-{Environment.ProcessId:x}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            Factory = new Factory(AllocateDirectory,
                path => File.WriteAllText(Path.Combine(path, "seed.txt"), "seed"), _root,
                gitRunner: Runner);
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
