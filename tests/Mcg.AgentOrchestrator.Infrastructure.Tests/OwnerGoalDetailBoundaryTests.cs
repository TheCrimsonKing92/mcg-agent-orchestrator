using System.Reflection;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class OwnerGoalDetailBoundaryTests
{
    [Fact]
    public void FormatterDelegatesReadsToQuery()
    {
        var formatter = ReadSource("OwnerConsoleGoalDetailFormatter.cs");

        Assert.Contains("OwnerGoalDetailQuery", formatter, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 { "LoadGoalsAsync", "ListGoalMetadataAsync", "ReadLast(", "ConductorBatchLoop", "OwnerGoalLifecycleEvent.TryParse" })
            Assert.DoesNotContain(forbidden, formatter, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryHasNoPresentationDependencyOrPartialDeclaration()
    {
        var query = ReadSource("OwnerGoalDetailQuery.cs");

        foreach (var forbidden in new[] { "IOwnerConsoleOutput", "WriteLine", "OwnerConsoleGoalLanding", "StagePhrase", "partial" })
            Assert.DoesNotContain(forbidden, query, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderAcceptsOnlySnapshotOutputAndTimeZone()
    {
        var render = typeof(OwnerConsoleGoalDetailFormatter).GetMethod("Render",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(render);
        Assert.Equal(new[] { typeof(OwnerGoalDetailSnapshot), typeof(IOwnerConsoleOutput), typeof(TimeZoneInfo) },
            render.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    private static string ReadSource(string filename) => File.ReadAllText(Path.Combine(
        VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Console", filename));
}
