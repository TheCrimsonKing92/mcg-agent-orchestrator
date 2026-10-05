using Mcg.AgentOrchestrator.Core;
using Repository = ReverseDependencyIndexScopeTests.Repository;

// Parallel-safe: unique fixture repository, no real-repository census or shared cache assertions.
public sealed class RepositoryTestImpactPlannerTwoHopNarrowSelectionTests
{
    [Xunit.Fact]
    public void Plan_TwoHopConsumer_ExcludesColocatedUnrelatedClassDeterministically()
    {
        using var repository = Repository.Create();
        repository.Write($"{Repository.TestDirectory}/RunGoalServiceTests.cs",
            "public sealed class RunGoalServiceTests { private RunGoalService _service; " +
            "[Xunit.Fact] public void Runs() { } } " +
            "public sealed class UnrelatedColocatedTests { [Xunit.Fact] public void Runs() { } }");

        var plan = RepositoryTestImpactPlanner.Plan([Repository.CoreSource], repository.Root);
        var repeated = RepositoryTestImpactPlanner.Plan([Repository.CoreSource], repository.Root);

        Xunit.Assert.True(plan.RequiresBuild);
        Xunit.Assert.False(plan.RequiresBroadVerification);
        Xunit.Assert.Equal(2, plan.Checks.Count);
        Xunit.Assert.Equal(new RepositoryTestImpactHeadroom(3, 4000, 1, 96, 1, 64),
            plan.ReverseDependencyHeadroom);
        Xunit.Assert.Contains(plan.Checks, check => check.TestProject == RepositoryTestProject.Core);
        var check = Xunit.Assert.Single(plan.Checks, check => check.TestProject == RepositoryTestProject.Infrastructure);
        var filterIndex = Array.IndexOf(check.Command.ToArray(), "--filter");
        Xunit.Assert.True(filterIndex >= 0);
        var filter = check.Command[filterIndex + 1];
        Xunit.Assert.Contains("FullyQualifiedName~RunGoalServiceTests", filter, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("UnrelatedColocatedTests", filter, StringComparison.Ordinal);
        Xunit.Assert.Equal(new[] { "RunGoalServiceTests" }, check.TestClassSelections);
        Xunit.Assert.Equal(plan.Checks.Select(item => item.CommandLine),
            repeated.Checks.Select(item => item.CommandLine));
    }
}
