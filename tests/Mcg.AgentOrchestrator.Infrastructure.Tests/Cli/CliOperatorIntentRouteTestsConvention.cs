using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

// Parallel-safe: pure routing and repository-source inspection.
public sealed class CliOperatorIntentRouteTestsConvention
{
    [Theory]
    [InlineData("operator-intent-status")]
    [InlineData("criterion-evidence-map")]
    [InlineData("OPERATOR-INTENT-STATUS")]
    public void ServedVerbIsRecognizedButHelpDeclines(string verb)
    {
        Assert.True(CliOperatorIntentRoute.IsServed([verb]));
        Assert.False(CliOperatorIntentRoute.IsServed([verb, "--help"]));
    }

    [Fact]
    public void OnlyThisSliceIsServed()
    {
        Assert.Equal(new[] { "criterion-evidence-map", "operator-intent-status" },
            CliOperatorIntentRoute.ServedVerbs.OrderBy(verb => verb, StringComparer.Ordinal));
        string[] followups = ["criterion-evidence-record", "criterion-evidence-repair", "adjudicate", "retry",
            "progress", "verify-manual", "attention", "approve-policy-change"];
        foreach (var verb in followups.Concat(CliCommandCapabilities.QueryOnlyVerbs))
            Assert.False(CliOperatorIntentRoute.IsServed([verb]));
        Assert.False(CliOperatorIntentRoute.IsServed([]));
    }

    [Fact]
    public void StartupReturnsThroughRouteBeforeWorkerTrackingRepositoryAndHydration()
    {
        var root = VerifiedRepositoryRoot.Find();
        var source = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Program.cs"));
        var consultation = source.IndexOf("if (CliOperatorIntentRoute.IsServed(startupArgs))", StringComparison.Ordinal);
        Assert.True(consultation >= 0);
        Assert.StartsWith("if (CliOperatorIntentRoute.IsServed(startupArgs))\n    return ExitCompletedStartupCommand(CliOperatorIntentRoute.Run(startupArgs, workspace));",
            source[consultation..].Replace("\r\n", "\n"), StringComparison.Ordinal);
        foreach (var later in new[] { "ProgramStartupLifecycle.EnsureStateDbInitialized(",
            "ProgramStartupLifecycle.InitializeWorkerProcessTracking(",
            "new SqliteOrchestratorStateRepository(", "CliReadOnlyStartupHydration.PrepareStartupAsync(" })
            Assert.True(source.IndexOf(later, StringComparison.Ordinal) > consultation, later);

        var route = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Cli/CliOperatorIntentRoute.cs"));
        foreach (var forbidden in new[] { "ProgramStartupLifecycle.", "WorkerProcessJobs.",
            "new SqliteOrchestratorStateRepository(", "CliReadOnlyStartupHydration.",
            "CliPersistentStateRunner.ExecuteCommand(", "SetWriteOperationTag(",
            "DrainAcceptanceRetryAuditOutbox(", "DrainGoalLifecycleEventOutbox(" })
            Assert.DoesNotContain(forbidden, route, StringComparison.Ordinal);
    }
}
