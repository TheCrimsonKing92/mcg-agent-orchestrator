using Mcg.AgentOrchestrator.Core;
using Repository = ReverseDependencyIndexScopeTests.Repository;

// Parallel-safe: unique temporary repositories, deterministic bounds, no shared environment changes.
public sealed class ReverseDependencyDegradationTests
{
    private const string BoundReason = "Reverse-dependency indexing exceeded the 2-source-file bound.";

    [Xunit.Fact]
    public void Read_InjectedIndexBound_ReportsTypedAbandonment()
    {
        using var repository = Repository.Create();

        var selection = ReverseDependencyTestImpactReader.Read(
            repository.Root, [Repository.CoreSource], bypassCache: true, maximumIndexedSourceFiles: 2);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Xunit.Assert.Equal(ReverseDependencyDegradationKind.IndexedSourceBound, selection.DegradationKind);
        Xunit.Assert.Equal(BoundReason, selection.Reason);
        Xunit.Assert.Equal(4_000, ReverseDependencyTestImpactReader.MaximumIndexedSourceFiles);
    }

    [Xunit.Fact]
    public void Read_SixChangedSources_ReportsChangedSourceFileCount()
    {
        using var repository = Repository.Create();
        var paths = Enumerable.Range(0, 6)
            .Select(index => $"src/Mcg.AgentOrchestrator.Core/Source{index}.cs").ToArray();
        foreach (var path in paths) repository.Write(path, "public class Source { }");

        var selection = ReverseDependencyTestImpactReader.Read(repository.Root, paths, bypassCache: true);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Xunit.Assert.Equal(ReverseDependencyDegradationKind.ChangedSourceFileCount, selection.DegradationKind);
        Xunit.Assert.Equal("Focused reverse-dependency selection supports 1-5 changed source files.", selection.Reason);
    }

    [Xunit.Fact]
    public void Plan_IndexBound_CarriesKindAndPreservesCheckReason()
    {
        using var repository = Repository.Create();
        var plan = RepositoryTestImpactPlanner.Plan(
            RepositoryChangeClassifier.Classify([Repository.CoreSource]),
            new FileSystemTestClassDeclarationReader(repository.Root, maximumIndexedSourceFiles: 2));

        Xunit.Assert.Equal(new RepositoryTestImpactDegradation(
            ReverseDependencyDegradationKind.IndexedSourceBound, BoundReason), plan.ReverseDependencyDegradation);
        var check = Xunit.Assert.Single(plan.Checks, check => check.Name == "infrastructure tests");
        Xunit.Assert.Equal(BoundReason, check.Reason);
        Xunit.Assert.DoesNotContain("--filter", check.Command);
    }

    [Xunit.Fact]
    public void Plan_SixChangedSources_CarriesRoutineDegradation()
    {
        using var repository = Repository.Create();
        var paths = Enumerable.Range(0, 6)
            .Select(index => $"src/Mcg.AgentOrchestrator.Core/Source{index}.cs").ToArray();
        foreach (var path in paths) repository.Write(path, "public class Source { }");

        var plan = RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify(paths),
            new FileSystemTestClassDeclarationReader(repository.Root));

        Xunit.Assert.Equal(new RepositoryTestImpactDegradation(
            ReverseDependencyDegradationKind.ChangedSourceFileCount,
            "Focused reverse-dependency selection supports 1-5 changed source files."), plan.ReverseDependencyDegradation);
    }

    [Xunit.Fact]
    public void Plan_UnreadableSource_CarriesRawReasonWithoutReceiptSuffix()
    {
        using var repository = Repository.Create();
        repository.Write(Repository.CoreSource, "public class DispatchFailureClassifier {");
        var selection = ReverseDependencyTestImpactReader.Read(
            repository.Root, [Repository.CoreSource], bypassCache: true);
        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, selection.Outcome);
        Xunit.Assert.Equal(ReverseDependencyDegradationKind.Unreadable, selection.DegradationKind);
        Xunit.Assert.NotNull(selection.CacheReceipt);
        Xunit.Assert.NotNull(selection.Reason);

        var plan = RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify([Repository.CoreSource]),
            new FileSystemTestClassDeclarationReader(repository.Root));

        Xunit.Assert.Equal(new RepositoryTestImpactDegradation(
            ReverseDependencyDegradationKind.Unreadable, selection.Reason), plan.ReverseDependencyDegradation);
        var check = Xunit.Assert.Single(plan.Checks, check => check.Name == "infrastructure tests");
        Xunit.Assert.StartsWith(selection.Reason + " reverse-dependency-cache=", check.Reason);
    }

    [Xunit.Fact]
    public void Plan_FocusedSelection_HasNoDegradation()
    {
        using var repository = Repository.Create();

        var plan = RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify([Repository.CoreSource]),
            new FileSystemTestClassDeclarationReader(repository.Root));

        Xunit.Assert.Null(plan.ReverseDependencyDegradation);
        Xunit.Assert.Contains(plan.Checks, check => check.Name == "focused reverse-dependent infrastructure tests");
    }
}
