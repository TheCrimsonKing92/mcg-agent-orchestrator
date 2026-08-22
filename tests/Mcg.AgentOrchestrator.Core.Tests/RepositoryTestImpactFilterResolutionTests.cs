namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryTestImpactFilterResolutionTests
{
    [Xunit.Fact]
    public void DottedChangedTestFileUsesDeclaredClass()
    {
        // Mirrors CliCommandTests.PersistentRunnerCommands.cs, whose declared class removes the dot.
        const string path =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PersistentRunnerCommands.cs";
        var reader = SourceDeclarationReader.ForFiles(
            (path, TestSource("CliCommandTestsPersistentRunnerCommands")));

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused changed infrastructure tests", check.Name);
        Assert.Equal(
            "FullyQualifiedName~CliCommandTestsPersistentRunnerCommands",
            check.Command[^1]);
        Assert.DoesNotContain('.', check.Command[^1]["FullyQualifiedName~".Length..]);
    }

    [Xunit.Fact]
    public void DottedMultiClassFileUsesEveryDeclaredClass()
    {
        // Mirrors CliCommandTests.GoalLifecycleCommands.cs, including its non-derivable classes.
        const string path =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalLifecycleCommands.cs";
        var reader = SourceDeclarationReader.ForFiles(
            (path, TestSource(
                "CliCommandTestsGoalLifecycleCommands",
                "CliCommandTestsIsolatedBuildLeaseCommands",
                "CliCommandTestsGoalLifecycleCleanupHooksAbandon",
                "CliCommandTestsGoalLifecycleCommandsCreation",
                "CliCommandTestsGoalLifecycleCleanupHooksAcceptance",
                "CliCommandTestsGoalLifecycleCleanupHooks")));

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused changed infrastructure tests", check.Name);
        Assert.Equal(
            "FullyQualifiedName~CliCommandTestsGoalLifecycleCleanupHooks|" +
            "FullyQualifiedName~CliCommandTestsGoalLifecycleCleanupHooksAbandon|" +
            "FullyQualifiedName~CliCommandTestsGoalLifecycleCleanupHooksAcceptance|" +
            "FullyQualifiedName~CliCommandTestsGoalLifecycleCommands|" +
            "FullyQualifiedName~CliCommandTestsGoalLifecycleCommandsCreation|" +
            "FullyQualifiedName~CliCommandTestsIsolatedBuildLeaseCommands",
            check.Command[^1]);
        Assert.All(
            check.Command[^1].Split('|'),
            token => Assert.DoesNotContain('.', token["FullyQualifiedName~".Length..]));
    }

    [Xunit.Fact]
    public void ChangedFileWithNoQualifyingClassAbandonsFocusedFilter()
    {
        const string path = "tests/Mcg.AgentOrchestrator.Core.Tests/CoreTestSupport.cs";
        var reader = SourceDeclarationReader.ForFiles(
            (path, "internal static class CoreTestSupport { }"));

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("core tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.DoesNotContain("CoreTestSupport", check.Command);
        Assert.Contains("declares no qualifying test class", check.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OrdinarySingleClassFiltersRemainByteIdentical()
    {
        const string taskBriefPath = "tests/Mcg.AgentOrchestrator.Core.Tests/TaskBriefTests.cs";
        const string classifierPath =
            "tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierTests.cs";
        const string docsPath =
            "tests/Mcg.AgentOrchestrator.Core.Tests/AgentHarnessDocsDriftTests.cs";
        var reader = SourceDeclarationReader.ForFiles(
            (taskBriefPath, TestSource("TaskBriefTests")),
            (classifierPath, TestSource("RepositoryChangeClassifierTests")),
            (docsPath, TestSource("AgentHarnessDocsDriftTests")));

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([taskBriefPath, classifierPath, docsPath]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused changed core tests", check.Name);
        Assert.Equal(
            "FullyQualifiedName~AgentHarnessDocsDriftTests|" +
            "FullyQualifiedName~RepositoryChangeClassifierTests|" +
            "FullyQualifiedName~TaskBriefTests",
            check.Command[^1]);
    }

    [Xunit.Fact]
    public void DotFreeMultiClassFileWidensToEveryDeclaredTestClass()
    {
        const string path =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs";
        var reader = SourceDeclarationReader.ForFiles(
            (path, TestSource(
                "AcceptanceOutputCaptureTests",
                "HermeticVerificationEnvironmentTests",
                "GoalAcceptanceVerifierTests",
                "GoalAcceptanceVerifierDotnetBuildSlotTests",
                "RealProcessShardAlphaSmokeTests",
                "RealProcessShardBetaSmokeTests")));

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal(
            "FullyQualifiedName~AcceptanceOutputCaptureTests|" +
            "FullyQualifiedName~GoalAcceptanceVerifierDotnetBuildSlotTests|" +
            "FullyQualifiedName~GoalAcceptanceVerifierTests|" +
            "FullyQualifiedName~HermeticVerificationEnvironmentTests|" +
            "FullyQualifiedName~RealProcessShardAlphaSmokeTests|" +
            "FullyQualifiedName~RealProcessShardBetaSmokeTests",
            check.Command[^1]);
    }

    [Xunit.Fact]
    public void EveryRepositoryTestFileIsNeverNarrowerThanLegacySelection()
    {
        if (!TryFindRepositoryRoot(out var root))
        {
            Assert.Skip("Repository census skipped because no repository root is reachable in this lane.");
            return;
        }

        var reader = new FileSystemTestClassDeclarationReader(root);
        var projectDirectories = new[]
        {
            "tests/Mcg.AgentOrchestrator.Core.Tests",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests"
        };

        foreach (var projectDirectory in projectDirectories)
        {
            var absoluteProjectDirectory = Path.Combine(root, projectDirectory);
            foreach (var path in Directory.EnumerateFiles(
                absoluteProjectDirectory,
                "*.cs",
                SearchOption.AllDirectories))
            {
                if (path.Split(Path.DirectorySeparatorChar).Any(segment =>
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                var plan = RepositoryTestImpactPlanner.Plan(
                    RepositoryChangeClassifier.Classify([relativePath]),
                    reader);
                var check = Assert.Single(plan.Checks);
                var filterIndex = -1;
                for (var argumentIndex = 0; argumentIndex < check.Command.Count; argumentIndex++)
                {
                    if (check.Command[argumentIndex].Equals("--filter", StringComparison.Ordinal))
                    {
                        filterIndex = argumentIndex;
                        break;
                    }
                }
                if (filterIndex < 0)
                {
                    continue;
                }

                var filterTokens = check.Command[filterIndex + 1].Split('|');
                var baseName = Path.GetFileNameWithoutExtension(path);
                var declarations = reader.ReadFile(relativePath);
                if (declarations.Outcome == TestClassDeclarationOutcome.Resolved)
                {
                    var selectedClassNames = filterTokens
                        .Select(token => token["FullyQualifiedName~".Length..])
                        .ToArray();
                    var legacySelectedClassNames = declarations.ClassNames
                        .Where(name => name.Contains(baseName, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    Assert.All(
                        legacySelectedClassNames,
                        name => Assert.Contains(name, selectedClassNames));
                }
                else
                {
                    Assert.Contains($"FullyQualifiedName~{baseName}", filterTokens);
                }
            }
        }
    }

    [Xunit.Fact]
    public void UnresolvedOrchestrationConventionAbandonsFocusedFilter()
    {
        var reader = new StubDeclarationReader(
            TestClassDeclarations.Resolved(["SomeOtherTests"]));
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "src/Mcg.AgentOrchestrator.App/Orchestration/NoMatchingClass.cs"
            ]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.DoesNotContain("NoMatchingClassTests", check.Command);
    }

    [Xunit.Fact]
    public void DottedOrchestrationSourceAbandonsFocusedFilter()
    {
        var reader = new StubDeclarationReader(TestClassDeclarations.Unreadable);
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.MergeTrains.cs"
            ]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.DoesNotContain("ConductorDriver.MergeTrainsTests", check.Command);
    }

    [Xunit.Fact]
    public void ExplicitRepositoryRootRejectsAmbientRelativePath()
    {
        var summary = RepositoryChangeClassifier.Classify([
            "tests/Mcg.AgentOrchestrator.Core.Tests/TaskBriefTests.cs"
        ]);

        var exception = Assert.Throws<ArgumentException>(() =>
            RepositoryTestImpactPlanner.Plan(summary, "."));

        Assert.Equal("repositoryRoot", exception.ParamName);
        Assert.Contains("absolute path", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ScannerIgnoresDeclarationsInRawStringsAndNestedTypes()
    {
        const string source = """""
            namespace Example;

            public sealed class RealTests
            {
                [Xunit.Fact]
                public void Runs()
                {
                    var interpolated = $"public sealed class InterpolatedPhantomTests {Describe("class")}";
                    var example = """
                        public sealed class PhantomTests
                        {
                            [Xunit.Fact] public void NeverRuns() { }
                        }
                        """;
                }

                private sealed class NestedTests
                {
                    [Xunit.Fact] public void Nested() { }
                }
            }

            public abstract class AbstractTests
            {
                [Xunit.Fact] public void Abstract() { }
            }

            public static class StaticTests
            {
                [Xunit.Fact] public static void Static() { }
            }

            public record struct RecordStructTests
            {
                [Xunit.Fact] public void RecordStruct() { }
            }
            """"";

        Assert.True(CSharpTestClassScanner.TryReadClassNames(source, out var classNames));
        Assert.Equal(["RealTests"], classNames);
    }

    [Xunit.Fact]
    public void MissingFileIsUnreadable()
    {
        var reader = SourceDeclarationReader.ForFiles();

        var declarations = reader.ReadFile(
            "tests/Mcg.AgentOrchestrator.Core.Tests/DoesNotExist.cs");

        Assert.Equal(TestClassDeclarationOutcome.Unreadable, declarations.Outcome);
        Assert.Empty(declarations.ClassNames);
    }

    private static string TestSource(params string[] classNames) =>
        string.Join(
            Environment.NewLine,
            classNames.Select(name =>
                $"public sealed class {name} {{ [Xunit.Fact] public void Runs() {{ }} }}"));

    private static bool TryFindRepositoryRoot(out string root)
    {
        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    root = directory.FullName;
                    return true;
                }

                directory = directory.Parent;
            }
        }

        root = string.Empty;
        return false;
    }

    private sealed class SourceDeclarationReader : ITestClassDeclarationReader
    {
        private readonly IReadOnlyDictionary<string, string> _sources;

        private SourceDeclarationReader(IReadOnlyDictionary<string, string> sources)
        {
            _sources = sources;
        }

        internal static SourceDeclarationReader ForFiles(
            params (string Path, string Source)[] files) =>
            new(files.ToDictionary(
                file => file.Path,
                file => file.Source,
                StringComparer.OrdinalIgnoreCase));

        public TestClassDeclarations ReadFile(string repositoryRelativePath)
        {
            if (!_sources.TryGetValue(repositoryRelativePath, out var source) ||
                !CSharpTestClassScanner.TryReadClassNames(source, out var classNames))
            {
                return TestClassDeclarations.Unreadable;
            }

            return classNames.Count == 0
                ? TestClassDeclarations.NoQualifyingClass
                : TestClassDeclarations.Resolved(classNames);
        }

        public TestClassDeclarations ReadProject(string repositoryRelativeDirectory) =>
            TestClassDeclarations.Unreadable;
    }

    private sealed class StubDeclarationReader(TestClassDeclarations projectDeclarations)
        : ITestClassDeclarationReader
    {
        public TestClassDeclarations ReadFile(string repositoryRelativePath) =>
            TestClassDeclarations.Unreadable;

        public TestClassDeclarations ReadProject(string repositoryRelativeDirectory) =>
            projectDeclarations;
    }
}
