using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Repository = ReverseDependencyIndexScopeTests.Repository;
using static RepositoryTestImpactHeadroomTests;

// Parallel-safe: fixture measurements are replayed without depending on shared LRU residency.
public sealed class ReverseDependencySmokeDegradationBranchTests
{
    [Xunit.Theory]
    [Xunit.InlineData(ReverseDependencyDegradationKind.IndexedSourceBound, 2, 96, 64)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.SelectedTestClassBound, 10, 1, 64)]
    [Xunit.InlineData(ReverseDependencyDegradationKind.FrontierSymbolBound, 10, 96, 1)]
    public void Plan_InjectedBound_SmokeBranchAcceptsOnlySignalledDegradation(
        ReverseDependencyDegradationKind kind, int indexedCap, int selectedCap, int frontierCap)
    {
        using var repository = Repository.Create();
        if (kind == ReverseDependencyDegradationKind.SelectedTestClassBound)
        {
            repository.Write($"{Repository.TestDirectory}/RunGoalServiceTests.cs",
                "public sealed class FirstClassifierTests { private DispatchFailureClassifier _classifier; " +
                "[Xunit.Fact] public void Runs() { } } " +
                "public sealed class SecondClassifierTests { private DispatchFailureClassifier _classifier; " +
                "[Xunit.Fact] public void Runs() { } }");
        }
        if (kind == ReverseDependencyDegradationKind.FrontierSymbolBound)
        {
            repository.Write(Repository.CoreSource,
                "public sealed class DispatchFailureClassifier { } public sealed class SecondSymbol { }");
        }
        var bypassed = ReverseDependencyTestImpactReader.Read(repository.Root, [Repository.CoreSource],
            bypassCache: true, maximumIndexedSourceFiles: indexedCap,
            maximumSelectedTestClasses: selectedCap, maximumFrontierSymbols: frontierCap);
        Xunit.Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, bypassed.Outcome);
        Xunit.Assert.Equal(kind, bypassed.DegradationKind);

        // Test the smoke branch and receipt rendering from one measured selection; cache lifecycle
        // is covered separately, and eviction by another parallel fact cannot change this verdict.
        var cold = PlanSelection(bypassed with
        {
            CacheReceipt = bypassed.CacheReceipt is { } coldReceipt
                ? coldReceipt with { Disposition = ReverseDependencyCacheDisposition.Miss } : null
        });
        var warm = PlanSelection(bypassed with
        {
            CacheReceipt = bypassed.CacheReceipt is { } warmReceipt
                ? warmReceipt with { Disposition = ReverseDependencyCacheDisposition.Hit } : null
        });
        var coldInfrastructure = InfrastructureCheck(cold);
        var warmInfrastructure = InfrastructureCheck(warm);
        Xunit.Assert.Equal(bypassed.DegradationKind, warm.ReverseDependencyDegradation?.Kind);
        Xunit.Assert.DoesNotContain("--filter", warmInfrastructure.Command);
        Xunit.Assert.Equal(cold.Checks.Select(check => check.CommandLine),
            warm.Checks.Select(check => check.CommandLine));
        if (bypassed.DegradationKind != ReverseDependencyDegradationKind.IndexedSourceBound)
        {
            Xunit.Assert.Contains("reverse-dependency-cache=miss", coldInfrastructure.Reason);
            Xunit.Assert.Contains("reverse-dependency-cache=hit", warmInfrastructure.Reason);
            Xunit.Assert.NotNull(bypassed.CacheReceipt);
            var expected = kind == ReverseDependencyDegradationKind.SelectedTestClassBound
                ? new RepositoryTestImpactHeadroom(3, 10, 2, 1, 1, 64)
                : new RepositoryTestImpactHeadroom(3, 10, null, 96, 2, 1);
            Xunit.Assert.Equal(expected, warm.ReverseDependencyHeadroom);
            Xunit.Assert.EndsWith(" " + expected.Render(), warmInfrastructure.Reason);
        }
        else
        {
            Xunit.Assert.Null(bypassed.CacheReceipt);
            Xunit.Assert.DoesNotContain("reverse-dependency-cache=", warmInfrastructure.Reason);
            Xunit.Assert.Equal(new RepositoryTestImpactHeadroom(3, 2, null, 96, null, 64),
                warm.ReverseDependencyHeadroom);
        }
        var receiptValues = new[]
        {
            bypassed.CacheReceipt?.IndexedFileCount.ToString(CultureInfo.InvariantCulture) ?? "n/a",
            bypassed.CacheReceipt?.IndexedSourceBytes.ToString(CultureInfo.InvariantCulture) ?? "n/a",
            bypassed.CacheReceipt?.ReparsedFileCount.ToString(CultureInfo.InvariantCulture) ?? "n/a",
            bypassed.CacheReceipt?.Fingerprint ?? "n/a"
        };
        if (kind == ReverseDependencyDegradationKind.IndexedSourceBound)
            Xunit.Assert.Equal(new[] { "n/a", "n/a", "n/a", "n/a" }, receiptValues);
        else
            Xunit.Assert.All(receiptValues, value => Xunit.Assert.NotEqual("n/a", value));
    }
}
