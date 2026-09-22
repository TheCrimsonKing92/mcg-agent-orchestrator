using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class RepositoryTestImpactFilterResolutionTests
{
    private readonly Xunit.ITestOutputHelper _output;

    public RepositoryTestImpactFilterResolutionTests(Xunit.ITestOutputHelper output)
    {
        _output = output;
    }

    [Xunit.Fact]
    public void AcceptanceOwnerTestUsesItsIndependentProject()
    {
        const string path =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/AcceptanceAttemptExecutionOwnerTests.cs";
        var reader = SourceDeclarationReader.ForFiles(
            (path, TestSource("AcceptanceAttemptExecutionOwnerTests")));

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal(RepositoryTestProject.Acceptance, check.TestProject);
        Assert.Contains(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Acceptance/Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj",
            check.Command);
        Assert.Equal("FullyQualifiedName~AcceptanceAttemptExecutionOwnerTests", check.Command[^1]);
    }

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
    public void RealDottedRepositoryFileUsesEveryDeclaredClass()
    {
        Assert.True(TryFindRepositoryRoot(out var root), "Repository root was not found.");
        const string path =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalLifecycleCommands.cs";
        Assert.True(File.Exists(Path.Combine(root, path)), $"Real dotted fixture was not found: {path}");

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            root);

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
    }

    [Xunit.Fact]
    public void ProjectReaderNormalizesTrailingDirectorySeparator()
    {
        Assert.True(TryFindRepositoryRoot(out var root), "Repository root was not found.");
        var reader = new FileSystemTestClassDeclarationReader(root);

        var declarations = reader.ReadProject(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/");

        Assert.Equal(TestClassDeclarationOutcome.Resolved, declarations.Outcome);
        Assert.Contains("ConductorDriverTests", declarations.ClassNames);
    }

    [Xunit.Fact]
    public void CoreServiceChangeSelectsTwoHopIntegrationConsumer()
    {
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";

        var first = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);
        var second = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        Assert.Equal(
            first.Checks.Select(check => check.CommandLine),
            second.Checks.Select(check => check.CommandLine));
        var dependentCheck = Assert.Single(first.Checks, check =>
            check.Command.Any(argument => argument.Contains(
                "RunGoalServiceTests",
                StringComparison.Ordinal)));
        Assert.Contains(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            dependentCheck.Command);
        Assert.Equal("FullyQualifiedName~RunGoalServiceTests", dependentCheck.Command[^1]);
        Assert.Equal(RepositoryTestProject.Infrastructure, dependentCheck.TestProject);
        Assert.Equal(["RunGoalServiceTests"], dependentCheck.TestClassSelections);
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheReusesEquivalentSnapshotWithoutChangingPlan()
    {
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";

        var first = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);
        var second = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        Assert.Equal(
            first.Checks.Select(check => check.CommandLine),
            second.Checks.Select(check => check.CommandLine));
        var dependentCheck = Assert.Single(second.Checks, check =>
            check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.Contains("reverse-dependency-cache=hit", dependentCheck.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheInvalidatesSameLengthContentWithOriginalTimestamp()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        const string consumerPath = "src/Mcg.AgentOrchestrator.App/Cli/RunGoalService.cs";
        var first = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);
        var absoluteConsumerPath = repository.GetPath(consumerPath);
        var originalTimestamp = File.GetLastWriteTimeUtc(absoluteConsumerPath);
        var originalSource = File.ReadAllText(absoluteConsumerPath);
        var replacement = new string('X', "DispatchFailureClassifier".Length);

        repository.Write(
            consumerPath,
            originalSource.Replace("DispatchFailureClassifier", replacement, StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(absoluteConsumerPath, originalTimestamp);
        var second = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        Assert.Equal(originalSource.Length, File.ReadAllText(absoluteConsumerPath).Length);
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(absoluteConsumerPath));
        Assert.Contains("RunGoalServiceTests", first.TestClassNames);
        Assert.DoesNotContain("RunGoalServiceTests", second.TestClassNames);
        Assert.Equal(ReverseDependencyCacheDisposition.Miss, second.CacheReceipt?.Disposition);
        Assert.NotEqual(first.CacheReceipt?.Fingerprint, second.CacheReceipt?.Fingerprint);
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheInvalidatesAddedSourceMembership()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        _ = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        repository.Write(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AddedConsumerTests.cs",
            "public sealed class AddedConsumerTests { " +
            "private readonly DispatchFailureClassifier _classifier = new(); " +
            "[Xunit.Fact] public void Runs() { } }");
        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        Assert.Equal(ReverseDependencyCacheDisposition.Miss, selection.CacheReceipt?.Disposition);
        Assert.Contains("AddedConsumerTests", selection.TestClassNames);
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheInvalidatesRemovedSourceMembership()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        _ = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        repository.Delete("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RunGoalServiceTests.cs");
        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        Assert.Equal(ReverseDependencyCacheDisposition.Miss, selection.CacheReceipt?.Disposition);
        Assert.DoesNotContain("RunGoalServiceTests", selection.TestClassNames);
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheInvalidatesRenamedSourceMembership()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        var first = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        repository.Move(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RunGoalServiceTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RenamedRunGoalServiceTests.cs");
        var second = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        Assert.Equal(ReverseDependencyCacheDisposition.Miss, second.CacheReceipt?.Disposition);
        Assert.NotEqual(first.CacheReceipt?.Fingerprint, second.CacheReceipt?.Fingerprint);
        Assert.Equal(first.TestClassNames, second.TestClassNames);
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheInvalidatesProjectGraphContent()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        var first = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);
        const string projectPath = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
        repository.Write(projectPath, File.ReadAllText(repository.GetPath(projectPath)) + Environment.NewLine);

        var second = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        Assert.Equal(ReverseDependencyCacheDisposition.Miss, second.CacheReceipt?.Disposition);
        Assert.NotEqual(first.CacheReceipt?.Fingerprint, second.CacheReceipt?.Fingerprint);
        Assert.Equal(first.TestClassNames, second.TestClassNames);
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheBindsToWorktreeRootAndGitHead()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var firstRepository = ReverseDependencyRepository.Create();
        using var secondRepository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        firstRepository.Write(".git/HEAD", "ref: refs/heads/main");
        firstRepository.Write(".git/refs/heads/main", new string('1', 40));

        var first = ReverseDependencyTestImpactReader.Read(firstRepository.Root, [changedPath]);
        var otherWorktree = ReverseDependencyTestImpactReader.Read(secondRepository.Root, [changedPath]);
        firstRepository.Write(".git/refs/heads/main", new string('2', 40));
        var nextCommit = ReverseDependencyTestImpactReader.Read(firstRepository.Root, [changedPath]);

        Assert.Equal(ReverseDependencyCacheDisposition.Miss, otherWorktree.CacheReceipt?.Disposition);
        Assert.Equal(ReverseDependencyCacheDisposition.Miss, nextCommit.CacheReceipt?.Disposition);
        Assert.NotEqual(first.CacheReceipt?.Fingerprint, otherWorktree.CacheReceipt?.Fingerprint);
        Assert.NotEqual(first.CacheReceipt?.Fingerprint, nextCommit.CacheReceipt?.Fingerprint);
        Assert.Equal(first.TestClassNames, nextCommit.TestClassNames);
    }

    [Xunit.Fact]
    public void CachedAndBypassedReverseDependencySelectionsAreOrdinallyEquivalent()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        _ = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);

        var cached = ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]);
        var bypassed = ReverseDependencyTestImpactReader.Read(
            repository.Root,
            [changedPath],
            bypassCache: true);

        Assert.Equal(ReverseDependencyCacheDisposition.Hit, cached.CacheReceipt?.Disposition);
        Assert.Equal(ReverseDependencyCacheDisposition.Bypass, bypassed.CacheReceipt?.Disposition);
        Assert.Equal(cached.Outcome, bypassed.Outcome);
        Assert.Equal(cached.TestClassNames, bypassed.TestClassNames);
    }

    [Xunit.Fact]
    public void ConcurrentReverseDependencyCacheReadsRemainEquivalentAndBounded()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        var selections = new ConcurrentBag<ReverseDependencyTestSelection>();

        Parallel.For(
            0,
            8,
            _ => selections.Add(ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath])));

        Assert.Equal(8, selections.Count);
        Assert.All(selections, selection =>
        {
            Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
            Assert.Equal(["RunGoalServiceTests"], selection.TestClassNames);
            Assert.InRange(selection.CacheReceipt!.RetainedSnapshotCount, 1, 4);
        });
    }

    [Xunit.Fact]
    public void ReverseDependencyCacheEvictsLeastRecentSnapshotAtFourEntryBound()
    {
        ReverseDependencyTestImpactReader.ClearCacheForTests();
        var repositories = Enumerable.Range(0, 5)
            .Select(_ => ReverseDependencyRepository.Create())
            .ToArray();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        try
        {
            var selections = repositories
                .Select(repository => ReverseDependencyTestImpactReader.Read(repository.Root, [changedPath]))
                .ToArray();
            var revisitedFirst = ReverseDependencyTestImpactReader.Read(
                repositories[0].Root,
                [changedPath]);

            Assert.All(selections, selection => Assert.InRange(
                selection.CacheReceipt!.RetainedSnapshotCount,
                1,
                4));
            Assert.Equal(4, selections[^1].CacheReceipt?.RetainedSnapshotCount);
            Assert.Equal(ReverseDependencyCacheDisposition.Miss, revisitedFirst.CacheReceipt?.Disposition);
            Assert.Equal(4, revisitedFirst.CacheReceipt?.RetainedSnapshotCount);
        }
        finally
        {
            foreach (var repository in repositories)
            {
                repository.Dispose();
            }
        }
    }

    [Xunit.Fact]
    public void RealRepositoryReverseDependencyCacheRecordsColdWarmPlanAndBypassEquivalence()
    {
        Assert.True(TryFindRepositoryRoot(out var root), "Repository root was not found.");
        Assert.True(Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")));
        Assert.True(File.Exists(Path.Combine(
            root,
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")));
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        Assert.True(File.Exists(Path.Combine(root, changedPath.Replace('/', Path.DirectorySeparatorChar))));
        ReverseDependencyTestImpactReader.ClearCacheForTests();

        var stopwatch = Stopwatch.StartNew();
        var cold = RepositoryTestImpactPlanner.Plan([changedPath], root);
        stopwatch.Stop();
        var coldInfrastructure = Assert.Single(cold.Checks, check =>
            check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.Contains("reverse-dependency-cache=miss", coldInfrastructure.Reason, StringComparison.Ordinal);
        var coldMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var warmMilliseconds = new List<double>();
        RepositoryTestImpactPlan? warm = null;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            stopwatch.Restart();
            warm = RepositoryTestImpactPlanner.Plan([changedPath], root);
            stopwatch.Stop();
            warmMilliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var bypassed = ReverseDependencyTestImpactReader.Read(root, [changedPath], bypassCache: true);
        var warmInfrastructure = Assert.Single(warm!.Checks, check =>
            check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.Equal(bypassed.TestClassNames, warmInfrastructure.TestClassSelections);
        Assert.Equal(
            cold.Checks.Select(check => check.CommandLine),
            warm.Checks.Select(check => check.CommandLine));
        Assert.Contains("reverse-dependency-cache=hit", warmInfrastructure.Reason, StringComparison.Ordinal);
        _output.WriteLine(
            "REVERSE_DEPENDENCY_CACHE_MEASUREMENT indexed_files={0} source_bytes={1} cold_plan_ms={2:F2} " +
            "warm_plan_ms={3} bypass_reparsed_files={4} fingerprint={5}",
            bypassed.CacheReceipt!.IndexedFileCount,
            bypassed.CacheReceipt.IndexedSourceBytes,
            coldMilliseconds,
            string.Join(',', warmMilliseconds.Select(value => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))),
            bypassed.CacheReceipt.ReparsedFileCount,
            bypassed.CacheReceipt.Fingerprint);
    }

    [Xunit.Fact]
    public void LocalCoreChangeWithoutDependentConsumersStaysNarrow()
    {
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/TestClassDeclarationReader.cs";
        repository.Write(
            changedPath,
            "namespace Mcg.AgentOrchestrator.Core; internal sealed class TestClassDeclarationReader { }");

        var plan = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("core tests", check.Name);
        Assert.DoesNotContain(
            plan.Checks,
            candidate => candidate.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
    }

    [Xunit.Fact]
    public void ChangedCoreSourceCountAboveFocusedBoundWidensToFullInfrastructureSuite()
    {
        using var repository = ReverseDependencyRepository.Create();
        var changedPaths = Enumerable.Range(1, 6)
            .Select(index => $"src/Mcg.AgentOrchestrator.Core/Application/Changed{index}.cs")
            .ToArray();
        foreach (var changedPath in changedPaths)
        {
            repository.Write(
                changedPath,
                $"namespace Mcg.AgentOrchestrator.Core; public sealed class Changed{Path.GetFileNameWithoutExtension(changedPath)[7..]} {{ }}");
        }

        var plan = RepositoryTestImpactPlanner.Plan(changedPaths, repository.Root);

        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.TestProject == RepositoryTestProject.Infrastructure);
        Assert.DoesNotContain("--filter", infrastructureCheck.Command);
        Assert.Contains("supports 1-5 changed source files", infrastructureCheck.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void UnavailableReverseDependencyEvidenceDoesNotClassifyAsDependencyFailure()
    {
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([changedPath]),
            new StubDeclarationReader(TestClassDeclarations.Unavailable));

        var check = Assert.Single(plan.Checks);
        Assert.Equal("core tests", check.Name);
        Assert.DoesNotContain(
            plan.Checks,
            candidate => candidate.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
    }

    [Xunit.Fact]
    public void MissingDependentProjectInPartialRootProducesUnavailableEvidence()
    {
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        repository.Delete(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        var reader = new FileSystemTestClassDeclarationReader(repository.Root);

        var selection = reader.ReadReverseDependentTestClasses([changedPath]);
        var plan = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        Assert.Equal(ReverseDependencySelectionOutcome.Unavailable, selection.Outcome);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("core tests", check.Name);
    }

    [Xunit.Fact]
    public void MissingDependentProjectInRepositoryRootFailsSafe()
    {
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        repository.Delete(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        repository.Write("Mcg.AgentOrchestrator.sln", string.Empty);
        var reader = new FileSystemTestClassDeclarationReader(repository.Root);

        var selection = reader.ReadReverseDependentTestClasses([changedPath]);
        var plan = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, selection.Outcome);
        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        Assert.DoesNotContain("--filter", infrastructureCheck.Command);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MissingDependentProjectInMarkedRepositoryFailsSafe(bool useWorktreeFile)
    {
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        const string dependentProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        repository.Delete(dependentProject);
        var gitMarker = Path.Combine(repository.Root, ".git");
        if (useWorktreeFile)
        {
            repository.Write(".git", "gitdir: C:/worktrees/damaged-real-repository");
        }
        else
        {
            Directory.CreateDirectory(gitMarker);
        }

        Assert.True(Directory.Exists(gitMarker) || File.Exists(gitMarker));
        Assert.False(File.Exists(Path.Combine(repository.Root, "Mcg.AgentOrchestrator.sln")));
        Assert.False(File.Exists(Path.Combine(
            repository.Root,
            dependentProject.Replace('/', Path.DirectorySeparatorChar))));
        var reader = new FileSystemTestClassDeclarationReader(repository.Root);

        var selection = reader.ReadReverseDependentTestClasses([changedPath]);
        var plan = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, selection.Outcome);
        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.Command.Contains(dependentProject));
        Assert.DoesNotContain("--filter", infrastructureCheck.Command);
    }

    [Xunit.Fact]
    public void DeletedCoreSourceProducesUnavailableReverseDependencyEvidence()
    {
        using var repository = ReverseDependencyRepository.Create();
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        repository.Delete(changedPath);
        var reader = new FileSystemTestClassDeclarationReader(repository.Root);

        var selection = reader.ReadReverseDependentTestClasses([changedPath]);
        var plan = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        Assert.Equal(ReverseDependencySelectionOutcome.Unavailable, selection.Outcome);
        var check = Assert.Single(plan.Checks);
        Assert.Equal("core tests", check.Name);
        Assert.DoesNotContain(
            plan.Checks,
            candidate => candidate.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
    }

    [Xunit.Fact]
    public void DeletedAndSurvivingCoreSourcesStillSelectIntegrationConsumer()
    {
        using var repository = ReverseDependencyRepository.Create();
        const string deletedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DeletedClassifier.cs";
        const string survivingPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";
        repository.Write(
            deletedPath,
            "namespace Mcg.AgentOrchestrator.Core; public sealed class DeletedClassifier { }");
        repository.Delete(deletedPath);

        var selection = new FileSystemTestClassDeclarationReader(repository.Root)
            .ReadReverseDependentTestClasses([deletedPath, survivingPath]);
        var plan = RepositoryTestImpactPlanner.Plan([deletedPath, survivingPath], repository.Root);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Contains("RunGoalServiceTests", selection.TestClassNames);
        Assert.Contains(plan.Checks, check =>
            check.Command.Contains("FullyQualifiedName~RunGoalServiceTests", StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void CoreServiceAndCoreUnitTestCochangeStillSelectsIntegrationConsumer()
    {
        using var repository = ReverseDependencyRepository.Create();
        var plan = RepositoryTestImpactPlanner.Plan(
            [
                "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs",
                "tests/Mcg.AgentOrchestrator.Core.Tests/DispatchFailureClassifierTests.cs"
            ],
            repository.Root);

        Assert.Contains(plan.Checks, check =>
            check.Command.Contains("FullyQualifiedName~RunGoalServiceTests", StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void TargetTestHelperDeclarationsRemainInSecondHopFrontier()
    {
        using var repository = ReverseDependencyRepository.Create();
        repository.Write(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DispatchClassifierTestSupport.cs",
            "public abstract class DispatchClassifierTestSupport { " +
            "protected readonly DispatchFailureClassifier Classifier = new(); }");
        repository.Write(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/HelperMediatedTests.cs",
            "public sealed class HelperMediatedTests : DispatchClassifierTestSupport { " +
            "[Xunit.Fact] public void Runs() { } }");

        var plan = RepositoryTestImpactPlanner.Plan(
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"],
            repository.Root);

        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        Assert.Contains(
            "FullyQualifiedName~HelperMediatedTests",
            RequiredTestImpactFilter(infrastructureCheck),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DirectConsumerSelectionDoesNotIncludeUnrelatedColocatedTestClasses()
    {
        using var repository = ReverseDependencyRepository.Create();
        repository.Write(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DirectConsumers.cs",
            "public sealed class DirectConsumerTests { " +
            "private readonly DispatchFailureClassifier _classifier = new(); " +
            "[Xunit.Fact] public void Runs() { } }" +
            string.Join(
                Environment.NewLine,
                Enumerable.Range(1, 16).Select(index =>
                    $"public sealed class Unrelated{index}Tests {{ " +
                    "[Xunit.Fact] public void Runs() { } }")));

        var plan = RepositoryTestImpactPlanner.Plan(
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"],
            repository.Root);

        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        var filter = RequiredTestImpactFilter(infrastructureCheck);
        Assert.Contains("FullyQualifiedName~DirectConsumerTests", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated", filter, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void EnumDeclarationParticipatesInReverseDependencyTraversal()
    {
        using var repository = ReverseDependencyRepository.Create();
        repository.Write(
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs",
            "namespace Mcg.AgentOrchestrator.Core; public enum DispatchOutcomeKind { Success, Failure }");
        repository.Write(
            "src/Mcg.AgentOrchestrator.App/Cli/RunGoalService.cs",
            "namespace Mcg.AgentOrchestrator.App; public sealed class RunGoalService { " +
            "private readonly DispatchOutcomeKind _outcome = DispatchOutcomeKind.Success; }");

        var plan = RepositoryTestImpactPlanner.Plan(
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"],
            repository.Root);

        Assert.Contains(plan.Checks, check =>
            check.Command.Contains("FullyQualifiedName~RunGoalServiceTests", StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void ReverseDependencyFanOutAboveBoundWidensAtomically()
    {
        using var repository = ReverseDependencyRepository.Create();
        repository.Write(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DirectConsumers.cs",
            string.Join(
                Environment.NewLine,
                Enumerable.Range(1, ReverseDependencyTestImpactReader.MaximumSelectedTestClasses + 1).Select(index =>
                    $"public sealed class DirectConsumer{index}Tests {{ " +
                    "private readonly DispatchFailureClassifier _classifier = new(); " +
                    "[Xunit.Fact] public void Runs() { } }")));
        const string changedPath =
            "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs";

        var plan = RepositoryTestImpactPlanner.Plan([changedPath], repository.Root);

        Assert.Equal(2, plan.Checks.Count);
        var infrastructureCheck = Assert.Single(plan.Checks, check =>
            check.Command.Contains(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        Assert.DoesNotContain("--filter", infrastructureCheck.Command);
        Assert.Contains(
            $"{ReverseDependencyTestImpactReader.MaximumSelectedTestClasses}-test-class bound",
            infrastructureCheck.Reason,
            StringComparison.Ordinal);
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
    public void CliSourceExposesTypedInfrastructureImpactScope()
    {
        var plan = RepositoryTestImpactPlanner.Plan(
        [
            "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal(RepositoryTestProject.Infrastructure, check.TestProject);
        Assert.Equal(["CliCommandTests", "CliHelpTests"], check.TestClassSelections);
        Assert.DoesNotContain(
            "DotnetBuildEnvironmentManagerTests",
            check.TestClassSelections!,
            StringComparer.Ordinal);
    }

    [Xunit.Fact]
    public void DashboardSourceDerivesFilterFromTypedImpactScope()
    {
        var plan = RepositoryTestImpactPlanner.Plan(
        [
            "src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardEndpoints.Goals.cs"
        ]);

        var check = Assert.Single(plan.Checks);
        Assert.Equal(RepositoryTestProject.Dashboard, check.TestProject);
        Assert.Equal(
            [
                "DashboardRenderingTests",
                "DashboardHostTests",
                "DashboardDispatchStartFailureEndpointTests",
                "DashboardValidationHarnessTests"
            ],
            check.TestClassSelections);
        Assert.Equal(
            "(FullyQualifiedName~DashboardRenderingTests|FullyQualifiedName~DashboardHostTests|" +
            "FullyQualifiedName~DashboardDispatchStartFailureEndpointTests|" +
            "FullyQualifiedName~DashboardValidationHarnessTests)&Category!=HostIntegration",
            check.Command[^1]);
    }

    [Xunit.Fact]
    public void DotFreeStemPreservesProjectWideLegacySelection()
    {
        const string path =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs";
        var reader = new StubDeclarationReader(
            TestClassDeclarations.Resolved([
                "GoalWorktreeTestsAcceptanceLanding",
                "GoalWorktreeTestsAcceptanceRetry"
            ]),
            TestClassDeclarations.Resolved(["GoalWorktreeTestsAcceptanceRetry"]));

        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused changed infrastructure tests", check.Name);
        Assert.Equal(
            "FullyQualifiedName~GoalWorktreeTestsAcceptanceRetry|" +
            "FullyQualifiedName~GoalWorktreeTests",
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
            _output.WriteLine(
                "Repository census did not run because no repository root is reachable in this lane.");
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
            var projectDeclarations = reader.ReadProject(projectDirectory);
            if (projectDeclarations.Outcome == TestClassDeclarationOutcome.Unreadable)
            {
                _output.WriteLine(
                    "Repository census did not run for '{0}': ReadProject returned Unreadable because " +
                    "one or more project sources were unavailable or could not be parsed in this lane.",
                    projectDirectory);
                return;
            }

            Assert.Equal(TestClassDeclarationOutcome.Resolved, projectDeclarations.Outcome);
            _output.WriteLine(
                "Repository census is comparing '{0}': ReadProject returned Resolved.",
                projectDirectory);
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
                var summary = RepositoryChangeClassifier.Classify([relativePath]);
                if (summary.RequiresBroadVerification)
                {
                    Assert.All(
                        RepositoryTestImpactPlanner.Plan(summary, reader).Checks,
                        check => Assert.DoesNotContain("--filter", check.Command));
                    continue;
                }

                var plan = RepositoryTestImpactPlanner.Plan(summary, reader);
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
                    var legacySelectedClassNames = projectDeclarations.ClassNames
                        .Where(name => name.Contains(baseName, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    Assert.All(
                        legacySelectedClassNames,
                        name => Assert.Contains(
                            selectedClassNames,
                            selector => name.Contains(selector, StringComparison.OrdinalIgnoreCase)));
                }
                else
                {
                    Assert.Contains($"FullyQualifiedName~{baseName}", filterTokens);
                }
            }
        }

        _output.WriteLine("Repository census completed using Resolved declarations for all test projects.");
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
    public void PartiallyResolvedOrchestrationConventionAbandonsFocusedFilter()
    {
        var reader = new StubDeclarationReader(
            TestClassDeclarations.Resolved(["ConductorDriverTests"]));
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs",
                "src/Mcg.AgentOrchestrator.App/Orchestration/LandingExecutor.cs"
            ]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.DoesNotContain("ConductorDriverTests", check.Command);
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
    public void UnreadableOrchestrationIndexAbandonsFocusedFilter()
    {
        var reader = new StubDeclarationReader(TestClassDeclarations.Unreadable);
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([
                "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"
            ]),
            reader);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("infrastructure tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.DoesNotContain("ConductorDriverTests", check.Command);
    }

    [Xunit.Fact]
    public void MissingDotFreeTestFileAbandonsFocusedFilter()
    {
        const string path = "tests/Mcg.AgentOrchestrator.Core.Tests/DoesNotExist.cs";
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            SourceDeclarationReader.ForFiles());

        var check = Assert.Single(plan.Checks);
        Assert.Equal("core tests", check.Name);
        Assert.DoesNotContain("--filter", check.Command);
        Assert.DoesNotContain("DoesNotExist", check.Command);
        Assert.Contains("could not be read", check.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void UnavailableReaderPreservesLegacyDotFreeFilter()
    {
        const string path = "tests/Mcg.AgentOrchestrator.Core.Tests/TaskBriefTests.cs";
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([path]),
            new StubDeclarationReader(
                TestClassDeclarations.Unavailable,
                TestClassDeclarations.Unavailable));

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused changed core tests", check.Name);
        Assert.Equal("FullyQualifiedName~TaskBriefTests", check.Command[^1]);
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
    public void ScannerReadsInterpolatedVerbatimStringsBeginningWithEscapedQuotes()
    {
        const string source = """""
            public sealed class VerbatimStringTests
            {
                [Xunit.Fact]
                public void Runs()
                {
                    var id = "session";
                    Assert.Contains($@"""providerSessionId"":""{id}""", "{}");
                }
            }
            """"";

        Assert.True(CSharpTestClassScanner.TryReadClassNames(source, out var classNames));
        Assert.Equal(["VerbatimStringTests"], classNames);
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

    private static bool TryFindRepositoryRoot(
        out string root,
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
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

    private sealed class StubDeclarationReader(
        TestClassDeclarations projectDeclarations,
        TestClassDeclarations? fileDeclarations = null)
        : ITestClassDeclarationReader
    {
        public TestClassDeclarations ReadFile(string repositoryRelativePath) =>
            fileDeclarations ?? TestClassDeclarations.Unreadable;

        public TestClassDeclarations ReadProject(string repositoryRelativeDirectory) =>
            projectDeclarations;
    }

    private sealed class ReverseDependencyRepository : IDisposable
    {
        private ReverseDependencyRepository(string root)
        {
            Root = root;
        }

        internal string Root { get; }

        internal static ReverseDependencyRepository Create()
        {
            var repository = new ReverseDependencyRepository(Path.Combine(
                Path.GetTempPath(),
                $"mcg-reverse-impact-{Guid.NewGuid():N}"));
            repository.Write(
                "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            repository.Write(
                "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj\" />" +
                "</ItemGroup></Project>");
            repository.Write(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj\" />" +
                "<ProjectReference Include=\"../../src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj\" />" +
                "</ItemGroup></Project>");
            repository.Write(
                "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs",
                "namespace Mcg.AgentOrchestrator.Core; public sealed class DispatchFailureClassifier { }");
            repository.Write(
                "src/Mcg.AgentOrchestrator.App/Cli/RunGoalService.cs",
                "namespace Mcg.AgentOrchestrator.App; public sealed class RunGoalService { " +
                "private readonly DispatchFailureClassifier _classifier = new(); }");
            repository.Write(
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RunGoalServiceTests.cs",
                "public sealed class RunGoalServiceTests { " +
                "private readonly RunGoalService _service = new(); " +
                "[Xunit.Fact] public void Runs() { } }");
            return repository;
        }

        internal void Write(string relativePath, string contents)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        internal void Delete(string relativePath)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Delete(path);
        }

        internal string GetPath(string relativePath) =>
            Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void Move(string sourceRelativePath, string destinationRelativePath)
        {
            var destination = GetPath(destinationRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(GetPath(sourceRelativePath), destination);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
