using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its temporary repository and bypasses the shared index cache.
public sealed class ReverseDependencyIndexScopeTests
{
    [Xunit.Fact]
    public void Read_UncompiledConsumers_PreservesSelectionAndIndexedCount()
    {
        using var repository = Repository.Create();
        var clean = ReverseDependencyTestImpactReader.Read(
            repository.Root, [Repository.CoreSource], bypassCache: true);
        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, clean.Outcome);
        Xunit.Assert.Equal(["RunGoalServiceTests"], clean.TestClassNames);
        Xunit.Assert.Equal(3, clean.CacheReceipt!.IndexedFileCount);

        repository.WriteLeakedConsumers("artifacts", ".scratch");
        var withIntermediates = ReverseDependencyTestImpactReader.Read(
            repository.Root, [Repository.CoreSource], bypassCache: true);

        Xunit.Assert.Equal(clean.Outcome, withIntermediates.Outcome);
        Xunit.Assert.Equal(clean.TestClassNames, withIntermediates.TestClassNames);
        Xunit.Assert.Equal(clean.CacheReceipt.IndexedFileCount,
            withIntermediates.CacheReceipt!.IndexedFileCount);
        // The declaration reader uses the same source set, even for a project-wide test read.
        var declarations = new FileSystemTestClassDeclarationReader(repository.Root)
            .ReadProject(Repository.TestDirectory);
        Xunit.Assert.Equal(TestClassDeclarationOutcome.Resolved, declarations.Outcome);
        Xunit.Assert.Equal(["RunGoalServiceTests"], declarations.ClassNames);
    }

    [Xunit.Fact]
    public void Read_SimilarlyNamedFolders_SelectsTheirConsumers()
    {
        using var repository = Repository.Create();
        repository.WriteLeakedConsumers("artifactsx", ".scratchx");

        var selection = ReverseDependencyTestImpactReader.Read(
            repository.Root, [Repository.CoreSource], bypassCache: true);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Xunit.Assert.Equal(["LeakedDispatchConsumerTests", "RunGoalServiceTests"], selection.TestClassNames);
        Xunit.Assert.Equal(5, selection.CacheReceipt!.IndexedFileCount);
    }

    [Xunit.Fact]
    public void Enumerate_PropsExclusions_SkipsExactSegmentsAndKeepsSiblings()
    {
        var props = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Directory.Build.props"));
        var segments = props.Descendants()
            .Where(element => element.Name.LocalName == "DefaultItemExcludes")
            .SelectMany(element => element.Value.Replace('\\', '/').Split(';'))
            .Select(entry => Regex.Match(entry.Trim(), @"^\*\*/([^/*]+)/\*\*$"))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Xunit.Assert.NotEmpty(segments);

        using var repository = Repository.Create();
        const string project = "scope-project";
        repository.Write($"{project}/Kept.cs", "public class Kept { }");
        foreach (var segment in segments.Concat(["bin", "obj"]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            repository.Write($"{project}/{segment}/nested/Skipped.cs", "public class Skipped { }");
            repository.Write($"{project}/{segment.ToUpperInvariant()}/UpperSkipped.cs", "public class UpperSkipped { }");
            repository.Write($"{project}/{segment}x/Kept.cs", "public class Kept { }");
        }
        var expected = new List<string> { "Kept.cs" };
        expected.AddRange(segments.Concat(["bin", "obj"])
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(segment => $"{segment}x/Kept.cs"));
        // These other build-like folder names are not compile exclusions in the props file.
        foreach (var folder in new[] { "generated", "TestResults", "verify-obj" })
        {
            repository.Write($"{project}/{folder}/Kept.cs", "public class Kept { }");
            expected.Add($"{folder}/Kept.cs");
        }
        var projectPath = repository.GetPath(project);
        var actual = FileSystemTestClassDeclarationReader.EnumerateProjectSourceFiles(projectPath)
            .Select(path => Path.GetRelativePath(projectPath, path).Replace('\\', '/'));

        Xunit.Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void Enumerate_ExcludedAncestor_KeepsProjectRelativeSources()
    {
        using var repository = Repository.Create();
        const string relativePath = "artifacts/project/Kept.cs";
        repository.Write(relativePath, "public class Kept { }");

        Xunit.Assert.Equal([repository.GetPath(relativePath)],
            FileSystemTestClassDeclarationReader.EnumerateProjectSourceFiles(
                repository.GetPath("artifacts/project")));
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)) return verifiedRoot;
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root for Directory.Build.props was not found.");
    }

    internal sealed class Repository : IDisposable
    {
        internal const string CoreSource = "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        internal const string TestDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("mcg-impact-scope-");
        internal string Root => _directory.FullName;

        internal static Repository Create()
        {
            var repository = new Repository();
            repository.Write("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            repository.Write("src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj\" />" +
                "</ItemGroup></Project>");
            repository.Write($"{TestDirectory}/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj\" />" +
                "<ProjectReference Include=\"../../src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj\" />" +
                "</ItemGroup></Project>");
            repository.Write(CoreSource,
                "namespace Mcg.AgentOrchestrator.Core; public sealed class DispatchFailureClassifier { }");
            repository.Write("src/Mcg.AgentOrchestrator.App/Cli/RunGoalService.cs",
                "namespace Mcg.AgentOrchestrator.App; public sealed class RunGoalService { " +
                "private readonly DispatchFailureClassifier _classifier = new(); }");
            repository.Write($"{TestDirectory}/RunGoalServiceTests.cs",
                "public sealed class RunGoalServiceTests { private readonly RunGoalService _service = new(); " +
                "[Xunit.Fact] public void Runs() { } }");
            return repository;
        }

        internal void WriteLeakedConsumers(string appFolder, string testFolder)
        {
            Write($"src/Mcg.AgentOrchestrator.App/{appFolder}/verify-obj/LeakedDispatchConsumer.cs",
                "public sealed class LeakedDispatchConsumer { private DispatchFailureClassifier _classifier; }");
            Write($"{TestDirectory}/{testFolder}/test-obj/LeakedDispatchConsumerTests.cs",
                "public sealed class LeakedDispatchConsumerTests { private LeakedDispatchConsumer _consumer; " +
                "private DispatchFailureClassifier _classifier; [Xunit.Fact] public void Runs() { } }");
        }

        internal string GetPath(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void Write(string relativePath, string contents)
        {
            var path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
