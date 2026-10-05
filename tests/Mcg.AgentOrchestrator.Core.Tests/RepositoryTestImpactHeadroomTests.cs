using Mcg.AgentOrchestrator.Core;
using Repository = ReverseDependencyIndexScopeTests.Repository;

// Parallel-safe: each fact owns its repository; fixed selection replays avoid shared cache state.
public sealed class RepositoryTestImpactHeadroomTests
{
    [Xunit.Fact]
    public void Plan_ResolvedSelection_AppendsExactMeasuredSuffix()
    {
        using var repository = Repository.Create();
        var selection = new FileSystemTestClassDeclarationReader(repository.Root,
            maximumIndexedSourceFiles: 10, maximumSelectedTestClasses: 5, maximumFrontierSymbols: 4)
            .ReadReverseDependentTestClasses([Repository.CoreSource]);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        var expected = new RepositoryTestImpactHeadroom(3, 10, 1, 5, 1, 4);
        Xunit.Assert.Equal(expected, selection.Headroom);
        var plan = PlanSelection(selection);
        var baseline = PlanSelection(selection with { Headroom = null });
        Xunit.Assert.Equal(expected, plan.ReverseDependencyHeadroom);
        Xunit.Assert.Null(baseline.ReverseDependencyHeadroom);
        Xunit.Assert.Null(plan.ReverseDependencyDegradation);
        Xunit.Assert.Equal(InfrastructureCheck(baseline).Reason +
            " headroom indexed-files=3/10 selected-classes=1/5 frontier-symbols=1/4",
            InfrastructureCheck(plan).Reason);
        Xunit.Assert.Equal(baseline.Checks.Select(check => check.CommandLine),
            plan.Checks.Select(check => check.CommandLine));
    }

    [Xunit.Fact]
    public void Plan_SelectedClassBound_ReportsCrossingCountAndExactSuffix()
    {
        using var repository = Repository.Create();
        repository.Write($"{Repository.TestDirectory}/OtherClassifierTests.cs",
            "public sealed class OtherClassifierTests { private DispatchFailureClassifier _classifier; " +
            "[Xunit.Fact] public void Runs() { } }");
        var selection = new FileSystemTestClassDeclarationReader(repository.Root,
            maximumIndexedSourceFiles: 10, maximumSelectedTestClasses: 1, maximumFrontierSymbols: 4)
            .ReadReverseDependentTestClasses([Repository.CoreSource]);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Xunit.Assert.Equal(ReverseDependencyDegradationKind.SelectedTestClassBound, selection.DegradationKind);
        var expected = new RepositoryTestImpactHeadroom(4, 10, 2, 1, 1, 4);
        Xunit.Assert.Equal(expected, selection.Headroom);
        Xunit.Assert.Empty(selection.TestClassNames);
        var plan = PlanSelection(selection);
        var baseline = PlanSelection(selection with { Headroom = null });
        Xunit.Assert.Equal(expected, plan.ReverseDependencyHeadroom);
        Xunit.Assert.Equal(selection.DegradationKind, plan.ReverseDependencyDegradation?.Kind);
        Xunit.Assert.Equal(InfrastructureCheck(baseline).Reason +
            " headroom indexed-files=4/10 selected-classes=2/1 frontier-symbols=1/4",
            InfrastructureCheck(plan).Reason);
        Xunit.Assert.DoesNotContain("--filter", InfrastructureCheck(plan).Command);
    }

    [Xunit.Fact]
    public void Plan_IndexedSourceBound_ReportsUnreachedCountsAndPreservesBareReason()
    {
        using var repository = Repository.Create();
        var selection = new FileSystemTestClassDeclarationReader(repository.Root, maximumIndexedSourceFiles: 2)
            .ReadReverseDependentTestClasses([Repository.CoreSource]);

        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Xunit.Assert.Equal(ReverseDependencyDegradationKind.IndexedSourceBound, selection.DegradationKind);
        var expected = new RepositoryTestImpactHeadroom(3, 2, null, 96, null, 64);
        Xunit.Assert.Equal(expected, selection.Headroom);
        Xunit.Assert.Null(selection.CacheReceipt);
        var plan = PlanSelection(selection);
        Xunit.Assert.Equal(expected, plan.ReverseDependencyHeadroom);
        Xunit.Assert.Equal("headroom indexed-files=3/2 selected-classes=n/a/96 frontier-symbols=n/a/64",
            plan.ReverseDependencyHeadroom!.Render());
        // No receipt means no suffix: the protected indexed-bound reason remains exact.
        Xunit.Assert.Equal("Reverse-dependency indexing exceeded the 2-source-file bound.",
            InfrastructureCheck(plan).Reason);
        Xunit.Assert.DoesNotContain("--filter", InfrastructureCheck(plan).Command);
    }

    [Xunit.Fact]
    public void Plan_ReaderNotInvoked_LeavesHeadroomAbsent()
    {
        using var repository = Repository.Create();
        var plan = RepositoryTestImpactPlanner.Plan(["README.md"], repository.Root);

        Xunit.Assert.Null(plan.ReverseDependencyHeadroom);
        Xunit.Assert.All(plan.Checks, check => Xunit.Assert.DoesNotContain("headroom ", check.Reason));
    }

    internal static RepositoryTestImpactPlan PlanSelection(ReverseDependencyTestSelection selection) =>
        RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify([Repository.CoreSource]),
            new SelectionReader(selection));

    internal static RepositoryTestImpactCheck InfrastructureCheck(RepositoryTestImpactPlan plan) =>
        Xunit.Assert.Single(plan.Checks, check => check.TestProject == RepositoryTestProject.Infrastructure);

    private sealed class SelectionReader(ReverseDependencyTestSelection selection) : ITestClassDeclarationReader
    {
        public TestClassDeclarations ReadFile(string path) => TestClassDeclarations.Unavailable;
        public TestClassDeclarations ReadProject(string directory) => TestClassDeclarations.Unavailable;
        public ReverseDependencyTestSelection ReadReverseDependentTestClasses(IReadOnlyList<string> paths) => selection;
    }
}
