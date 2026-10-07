// Parallel-safe: reads source from the explicitly verified repository root only.
public sealed class WorkerAdoptionTransferBoundaryTests
{
    [Xunit.Fact]
    public void Transfer_MutationBoundary_OnlyCallsOwnerTransfer()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Orchestration", "WorkerAdoptionTransfer.cs"));
        Assert.Contains("TryTransferDetachedOwner(", source, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "TryMarkReleasedEntry", "MarkReleased", "RecordDiagnostic", "TryMarkConductorDetached",
            "TryMarkRuntimeOwned", ".Register(", "Kill(", "WorkerProcessJobs", "ReportTaskProgress",
            ".RecordTask", "SaveAsync", "Console."
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RecorderConstruction_AllCliSites_SupplyDefaultTransfer()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Cli", "CliCommandHandlers.Goals.cs"));
        var recorders = Count(source, "new ConductorLifecycleRecorder(");
        Assert.True(recorders >= 1, "At least one CLI recorder construction must be inspected.");
        Assert.Equal(recorders, Count(source, "WorkerAdoptionTransfer.CreateDefault("));
    }

    private static int Count(string source, string text) =>
        source.Split(text, StringSplitOptions.None).Length - 1;
}
