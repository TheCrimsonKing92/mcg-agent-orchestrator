using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AssemblyCleanupApparatusRoutingTests
{
    [Fact]
    public void GreenProbeRetainsFailedCleanupVerdictAndInvocationJournalBindings()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        Directory.CreateDirectory(fixture.Root);
        var partition = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Remainder", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~Remainder"]
        };
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(new GoalId("12345678123456781234567812345678"),
                fixture.Root, [partition], 5, true, _ => "candidate-tree", _ => "main", _ => "candidate-commit",
                () => "attempt", () => "manifest", () => false)));
        var originalPath = fixture.WriteReceipt([
            AssemblyCleanupTrxFixture.Result("Tests.Passes", "Passed"),
            AssemblyCleanupTrxFixture.Result("[Test Assembly Cleanup Failure (Tests.Passes)]", "Failed")]);
        var probePath = fixture.WriteReceipt([AssemblyCleanupTrxFixture.Result("Tests.Passes", "Passed")]);
        var original = new AcceptanceCheckResult(partition.Name, false, 2, null, TestResultPaths: [originalPath],
            FailureClassification: AcceptanceFailureClassifications.AssemblyCleanupFailure,
            TestResultAttemptId: "attempt", FailingTestIdentities: ["cleanup"],
            CompletionDecision: new(false, AcceptanceShardCompletionPredicates.AssemblyCleanupFailure, false, 2, 2, 2, "Failed"));
        var probe = new AcceptanceCheckResult(partition.Name, true, 0, null, TestResultPaths: [probePath],
            TestResultAttemptId: "attempt", TestResultRunOrdinal: 1,
            CompletionDecision: new(true, null, false, 0, 1, 1, "Passed"));
        cache.RecordWithinAttemptRetry(partition, original, "attempt:remainder:0", "attempt:remainder:1",
            new AcceptanceRetainedDiagnostic(Path.Combine(fixture.Root, "stderr.log"), "diagnostic-hash"));
        var verdict = cache.SelectPartitionVerdict(partition, original, probe);
        Assert.False(verdict.Passed);
        Assert.Equal(2, verdict.ExitCode);
        Assert.Same(original.CompletionDecision, verdict.CompletionDecision);
        Assert.Equal(original.TestResultPaths, verdict.TestResultPaths);
        Assert.Equal("attempt", verdict.TestResultAttemptId);
        Assert.Equal(0, verdict.TestResultRunOrdinal);
        Assert.Null(verdict.WithinAttemptRerun);
        var journal = File.ReadAllText(cache.JournalPath);
        Assert.Contains("attempt:remainder:0", journal, StringComparison.Ordinal);
        Assert.Contains("attempt:remainder:1", journal, StringComparison.Ordinal);
        Assert.Contains("candidate-tree", journal, StringComparison.Ordinal);
        Assert.Contains("main", journal, StringComparison.Ordinal);
        Assert.Contains("flake_confirmed=true", journal, StringComparison.Ordinal);
        Assert.Contains("verdict_source=first_run", journal, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptanceRun_CleanupClassification_UsesExistingApparatusRoute()
    {
        var check = new AcceptanceCheckResult("infrastructure tests: Remainder", false, 2, null,
            FailureClassification: "assembly-cleanup-failure");
        var summary = new AcceptanceVerificationSummary(false, [check], FailedChecks: [check.Name]);

        Assert.True(ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(summary));
    }

    [Fact]
    public void AcceptanceRun_AdditionalRealFailure_DoesNotUseApparatusRoute()
    {
        var cleanup = new AcceptanceCheckResult("infrastructure tests: Remainder", false, 2, null,
            FailureClassification: "assembly-cleanup-failure");
        var real = new AcceptanceCheckResult("infrastructure tests: Real failure", false, 1, null);
        var summary = new AcceptanceVerificationSummary(false, [cleanup, real],
            FailedChecks: [cleanup.Name, real.Name]);

        Assert.False(ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(summary));
    }

    [Theory]
    [InlineData(true, true, "assembly-cleanup-failure", true)]
    [InlineData(false, true, "assembly-cleanup-failure", false)]
    [InlineData(true, false, "assembly-cleanup-failure", false)]
    [InlineData(true, true, "failing-trx", false)]
    public void PartitionRerun_CleanupPredicate_PreservesExistingEligibility(
        bool infrastructure, bool rerunEnabled, string predicate, bool expected)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        Directory.CreateDirectory(fixture.Root);
        var infrastructurePartition = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Process spawning",
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~ProcessSpawning"]
        };
        var partition = infrastructure ? infrastructurePartition : new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "core tests", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
        };
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                new GoalId("12345678123456781234567812345678"), fixture.Root, [infrastructurePartition], 5, rerunEnabled,
                _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest", () => false)));
        var decision = new AcceptanceShardCompletionDecision(false, predicate, false, 2, 444, 444, "Failed");

        Assert.Equal(expected, cache.ShouldRerunWithinAttempt(partition, decision));
    }
}
