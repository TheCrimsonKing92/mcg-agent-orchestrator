using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure path classification.
public sealed class AcceptanceAdmissionPathSetTests
{
    public static IEnumerable<object[]> AdmissionPaths()
    {
        string[] appFiles =
        [
            "AcceptanceHolderIdentity.cs", "AcceptanceHolderLease.cs", "AcceptanceJobCustodianHost.cs",
            "ConductorBatchLoop.LiveAcceptanceAdmission.cs", "ConductorBatchLoop.ParallelAcceptanceAdmission.cs",
            "ConductorDriver.AcceptanceCohortExecution.cs", "ConductorDriver.FollowerGates.cs",
            "ConductorDriver.GroupedGateAttempts.cs", "ConductorDriver.MergeTrains.cs",
            "ConductorGroupedGateAttemptHost.cs", "ConductorGroupedGateAttempts.cs",
            "ConductorLiveGroupedGateCount.cs", "ConductorOrphanGateAttemptAdoption.cs",
            "ConductorParallelAcceptanceAttempts.cs", "ConductorParallelAcceptanceAttempts.HolderLease.cs",
            "ConductorParallelAcceptanceAttempts.RunningExitPaths.cs", "LegacyAcceptanceHolderSettlement.cs",
            "OwnedChildAttemptCoordinatorFactory.cs", "StorageRetentionMaintenance.AcceptanceHolders.cs"
        ];
        var paths = appFiles.Select(name => "src/Mcg.AgentOrchestrator.App/Orchestration/" + name)
            .Concat(new[] { "AcceptanceJobCustodian.cs", "AcceptanceJobCustodianNative.cs",
                "AcceptanceJobCustodianLaunchSpecification.cs", "DurableAcceptanceJob.cs" }
                .Select(name => "src/Mcg.AgentOrchestrator.Execution/Processes/" + name));
        foreach (var path in paths)
        {
            yield return [path];
            yield return [path.Replace('/', '\\').ToUpperInvariant()];
        }
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(AdmissionPaths))]
    public void AdmissionFile_BothSlashStylesAndCase_Touches(string path) =>
        Assert.True(AcceptanceAdmissionPathSet.Touches([path]));

    [Xunit.Theory]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Execution/Processes/OwnedProcessGroup.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.Activation.cs")]
    [Xunit.InlineData("docs/operator-runbook.md")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AcceptanceHolderBindingCandidateRewriteTests.cs")]
    public void UnrelatedFile_DoesNotTouch(string path) =>
        Assert.False(AcceptanceAdmissionPathSet.Touches([path]));

    [Xunit.Theory]
    [Xunit.InlineData("ConductorBatchLoop.FallbackAcceptance.cs")]
    [Xunit.InlineData("ConductorBatchLoop.FollowerGateAdmission.cs")]
    public void AdditionalAdmissionPrefix_Touches(string name) => Assert.True(
        AcceptanceAdmissionPathSet.Touches(["src/Mcg.AgentOrchestrator.App/Orchestration/" + name]));

    [Xunit.Fact]
    public void EmptyPaths_AndPolicyDurations_MatchContract()
    {
        Assert.False(AcceptanceAdmissionPathSet.Touches([]));
        Assert.Equal(TimeSpan.FromMinutes(5), AcceptanceAdmissionPathSet.AdmissionTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), AcceptanceAdmissionPathSet.CompletionTimeout);
    }
}
