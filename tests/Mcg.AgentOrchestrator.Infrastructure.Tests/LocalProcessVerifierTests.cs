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
            Assert.True(prepared.Command.Contains("'--disable-build-servers'", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains("'-maxcpucount:1'", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains("'-p:UseSharedCompilation=false'", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase));
            Assert.True(prepared.ArtifactPathEvidence.Contains("Build environment lease: goal-12345678", StringComparison.Ordinal));
            Assert.True(prepared.ArtifactPathEvidence.Contains("Verification artifacts:", StringComparison.Ordinal));
            Assert.True(prepared.BuildEnvironment?.ExecutionLockPath.Contains(Path.Combine("slots", "slot-"), StringComparison.OrdinalIgnoreCase) == true);
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
            var expectedArtifacts = DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId);

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

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_leaves_dotnet_commands_without_goal_context_unchanged")]
    public void LocalProcessVerifierLeavesDotnetCommandsWithoutGoalContextUnchanged()
    {
        var prepared = LocalProcessVerifier.PrepareCommand("dotnet build Mcg.AgentOrchestrator.sln -c Release");

        Assert.Equal("dotnet build Mcg.AgentOrchestrator.sln -c Release", prepared.Command);
        Assert.False(prepared.Command.Contains("--disable-build-servers", StringComparison.Ordinal));
        Assert.Equal(string.Empty, prepared.ArtifactPathEvidence);
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_retries_once_on_CS2012_and_returns_passed")]
    public async Task LocalProcessVerifierRetriesOnceOnCs2012AndReturnsPassed()
    {
        var goalId = new GoalId("fedcba98fedcba98fedcba98fedcba98");
        var calls = new List<string[]>();
        var responses = new Queue<LocalProcessVerifier.CommandResult>([
            new(0, "", ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing", ""),
            new(0, "", ""),
            new(0, "Test run succeeded.", "")
        ]);

        var verifier = new LocalProcessVerifier((args, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult(responses.Dequeue());
        });

        try
        {
            var record = await verifier.RunAsync(
                "dotnet test Example.sln",
                "C:\\fake\\dir",
                goalId,
                TaskId.New());

            Assert.Equal(0, record.ExitCode);
            Assert.Equal(4, calls.Count);
            Assert.True(calls[0].SequenceEqual(["dotnet", "build-server", "shutdown"]));
            Assert.True(calls[2].SequenceEqual(["dotnet", "build-server", "shutdown"]));
            Assert.Equal("powershell.exe", calls[1][0]);
            Assert.Equal("powershell.exe", calls[3][0]);
            Assert.True(calls[1].SequenceEqual(calls[3]));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
