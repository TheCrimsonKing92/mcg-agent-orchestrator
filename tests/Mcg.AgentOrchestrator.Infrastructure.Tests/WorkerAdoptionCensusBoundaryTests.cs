// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class WorkerAdoptionCensusBoundaryTests
{
    [Xunit.Fact]
    public void Census_ReadOnlyBoundary_ContainsNoWorkerOrRegistryMutations()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Orchestration", "WorkerAdoptionCensus.cs"));

        foreach (var forbidden in new[]
        {
            "TryMarkReleasedEntry", "MarkReleased", "RecordDiagnostic", "TryMarkConductorDetached",
            "TryMarkRuntimeOwned", ".Register(", "Kill(", "WorkerProcessJobs", "Console."
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RecorderConstruction_AllCliSites_SupplyDefaultCensus()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Cli", "CliCommandHandlers.Goals.cs"));
        var recorders = Count(source, "new ConductorLifecycleRecorder(");
        Assert.True(recorders >= 1, "At least one CLI recorder construction must be inspected.");
        Assert.Equal(recorders, Count(source, "WorkerAdoptionCensus.CreateDefault("));
    }

    private static int Count(string source, string text) =>
        source.Split(text, StringSplitOptions.None).Length - 1;
}
