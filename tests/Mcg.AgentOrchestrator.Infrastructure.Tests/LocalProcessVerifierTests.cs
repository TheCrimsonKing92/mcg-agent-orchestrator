using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LocalProcessVerifierTests
{
    [Xunit.Fact(DisplayName = "LocalProcessVerifier_wraps_goal_dotnet_verification_with_isolated_artifacts")]
    public void LocalProcessVerifierWrapsGoalDotnetVerificationWithIsolatedArtifacts()
    {
        var goalId = new GoalId("12345678123456781234567812345678");
        var taskId = new TaskId("abcdef01abcdef01abcdef01abcdef01");
        try
        {
            var prepared = LocalProcessVerifier.PrepareCommand(" dotnet test Example.sln --verbosity minimal ", goalId, taskId);

            Assert.True(prepared.Command.StartsWith("dotnet test Example.sln --verbosity minimal", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains("'--artifacts-path'", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains("'-maxcpucount:1'", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains("'-p:UseSharedCompilation=false'", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains(Path.Combine("goals", "12345678", "lease", "artifacts"), StringComparison.OrdinalIgnoreCase));
            Assert.True(prepared.ArtifactPathEvidence.Contains("Build environment lease: goal-12345678", StringComparison.Ordinal));
            Assert.True(prepared.ArtifactPathEvidence.Contains("Verification artifacts:", StringComparison.Ordinal));
            Assert.True(prepared.BuildEnvironment?.ExecutionLockPath.Contains(Path.Combine("goals", "12345678", "lease", "lease.execution.lock"), StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_reuses_goal_build_lease_across_tasks")]
    public void LocalProcessVerifierReusesGoalBuildLeaseAcrossTasks()
    {
        var goalId = new GoalId("b16b00b5b16b00b5b16b00b5b16b00b5");
        try
        {
            var developer = LocalProcessVerifier.PrepareCommand(
                "dotnet test Example.sln --verbosity minimal",
                goalId,
                new TaskId("11111111111111111111111111111111"));
            var tester = LocalProcessVerifier.PrepareCommand(
                "dotnet test Example.sln --verbosity minimal",
                goalId,
                new TaskId("22222222222222222222222222222222"));
            var reviewer = LocalProcessVerifier.PrepareCommand(
                "dotnet test Example.sln --verbosity minimal",
                goalId,
                new TaskId("33333333333333333333333333333333"));
            var expectedArtifacts = Path.Combine("goals", "b16b00b5", "lease", "artifacts");

            Assert.True(developer.Command.Contains(expectedArtifacts, StringComparison.OrdinalIgnoreCase));
            Assert.True(tester.Command.Contains(expectedArtifacts, StringComparison.OrdinalIgnoreCase));
            Assert.True(reviewer.Command.Contains(expectedArtifacts, StringComparison.OrdinalIgnoreCase));
            Assert.True(developer.ArtifactPathEvidence.Contains("Build environment lease: goal-b16b00b5", StringComparison.Ordinal));
            Assert.True(tester.ArtifactPathEvidence.Contains("Build environment lease: goal-b16b00b5", StringComparison.Ordinal));
            Assert.True(reviewer.ArtifactPathEvidence.Contains("Build environment lease: goal-b16b00b5", StringComparison.Ordinal));
            Assert.Equal(developer.BuildEnvironment!.ExecutionLockPath, tester.BuildEnvironment!.ExecutionLockPath);
            Assert.Equal(developer.BuildEnvironment.ExecutionLockPath, reviewer.BuildEnvironment!.ExecutionLockPath);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_records_structured_broker_evidence_for_managed_dotnet_checks")]
    public async Task LocalProcessVerifierRecordsStructuredBrokerEvidenceForManagedDotnetChecks()
    {
        var root = CreateTempDirectory();
        var goalId = new GoalId("09080706090807060908070609080706");
        try
        {
            var record = await new LocalProcessVerifier().RunAsync(
                "dotnet test MissingProject.csproj --verbosity minimal",
                root,
                goalId,
                TaskId.New());

            Assert.True(record.StandardOutput.Contains("Build environment lease: goal-09080706", StringComparison.Ordinal));
            Assert.True(record.StandardOutput.Contains("Verification broker: local-process-verifier", StringComparison.Ordinal));
            Assert.True(record.StandardOutput.Contains("Broker duration ms:", StringComparison.Ordinal));
            Assert.True(record.StandardOutput.Contains("Broker exit code:", StringComparison.Ordinal));
            Assert.True(record.StandardOutput.Contains("Broker execution lock:", StringComparison.Ordinal));
            Assert.True(record.StandardOutput.Contains("Verification artifacts:", StringComparison.Ordinal));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_leaves_non_dotnet_verification_commands_unchanged")]
    public void LocalProcessVerifierLeavesNonDotnetVerificationCommandsUnchanged()
    {
        var prepared = LocalProcessVerifier.PrepareCommand("Write-Output ok", new GoalId("12345678123456781234567812345678"), TaskId.New());

        Assert.Equal("Write-Output ok", prepared.Command);
        Assert.Equal(string.Empty, prepared.ArtifactPathEvidence);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
