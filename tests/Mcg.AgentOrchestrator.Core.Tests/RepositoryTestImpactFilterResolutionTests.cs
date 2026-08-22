namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryTestImpactFilterResolutionTests
{
    [Xunit.Fact]
    public void DottedChangedTestFileUsesEveryDeclaredClass()
    {
        var reader = new FileSystemTestClassDeclarationReader(FindRepositoryRoot());
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalLifecycleCommands.cs"
            ]),
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
        var reader = new FileSystemTestClassDeclarationReader(FindRepositoryRoot());
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "tests/Mcg.AgentOrchestrator.Core.Tests/CoreTestSupport.cs"
            ]),
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
        var reader = new FileSystemTestClassDeclarationReader(FindRepositoryRoot());
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "tests/Mcg.AgentOrchestrator.Core.Tests/TaskBriefTests.cs",
                "tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierTests.cs",
                "tests/Mcg.AgentOrchestrator.Core.Tests/AgentHarnessDocsDriftTests.cs"
            ]),
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
        var reader = new FileSystemTestClassDeclarationReader(FindRepositoryRoot());
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs"
            ]),
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
        var root = FindRepositoryRoot();
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
        var reader = new FileSystemTestClassDeclarationReader(FindRepositoryRoot());
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
        var reader = new FileSystemTestClassDeclarationReader(FindRepositoryRoot());

        var declarations = reader.ReadFile("tests/Mcg.AgentOrchestrator.Core.Tests/DoesNotExist.cs");

        Assert.Equal(TestClassDeclarationOutcome.Unreadable, declarations.Outcome);
        Assert.Empty(declarations.ClassNames);
    }

    private static string FindRepositoryRoot()
    {
        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
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
