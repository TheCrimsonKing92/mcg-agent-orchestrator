using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedDotnetRoot")]
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
            Assert.False(prepared.Command.Contains('\'', StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains("--artifacts-path", StringComparison.Ordinal));
            Assert.False(prepared.Command.Contains("--disable-build-servers", StringComparison.Ordinal));
            Assert.False(prepared.Command.Contains("-maxcpucount:1", StringComparison.Ordinal));
            Assert.False(prepared.Command.Contains("-p:UseSharedCompilation=false", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains("-maxcpucount:", StringComparison.Ordinal));
            Assert.True(prepared.Command.Contains(DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("dotnet", prepared.FileName);
            Assert.Equal("test", prepared.Arguments[0]);
            Assert.Contains("--artifacts-path", prepared.Arguments);
            Assert.DoesNotContain("--disable-build-servers", prepared.Arguments);
            Assert.DoesNotContain("-maxcpucount:1", prepared.Arguments);
            Assert.DoesNotContain("-p:UseSharedCompilation=false", prepared.Arguments);
            Assert.Contains(prepared.Arguments, argument => argument.StartsWith("-maxcpucount:", StringComparison.Ordinal));
            Assert.True(prepared.ArtifactPathEvidence.Contains("Build environment lease: goal-12345678", StringComparison.Ordinal));
            Assert.True(prepared.ArtifactPathEvidence.Contains("Verification artifacts:", StringComparison.Ordinal));
            Assert.True(prepared.BuildEnvironment?.ExecutionLockPath.StartsWith(DotnetBuildEnvironmentManager.GoalRoot(goalId), StringComparison.OrdinalIgnoreCase) == true);
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
        Assert.Equal("Write-Output", prepared.FileName);
        Assert.True(prepared.Arguments.SequenceEqual(["ok"]));
        Assert.Equal(string.Empty, prepared.ArtifactPathEvidence);
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_leaves_dotnet_commands_without_goal_context_unchanged")]
    public void LocalProcessVerifierLeavesDotnetCommandsWithoutGoalContextUnchanged()
    {
        var prepared = LocalProcessVerifier.PrepareCommand("dotnet test Mcg.AgentOrchestrator.sln --filter 'AgentCatalog|WorkerProfile'");

        Assert.Equal("dotnet test Mcg.AgentOrchestrator.sln --filter 'AgentCatalog|WorkerProfile'", prepared.Command);
        Assert.Equal("dotnet", prepared.FileName);
        Assert.True(prepared.Arguments.SequenceEqual(["test", "Mcg.AgentOrchestrator.sln", "--filter", "AgentCatalog|WorkerProfile"]));
        Assert.False(prepared.Command.Contains("--disable-build-servers", StringComparison.Ordinal));
        Assert.Equal(string.Empty, prepared.ArtifactPathEvidence);
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_launches_dotnet_directly_without_a_shell_host")]
    public async Task LocalProcessVerifierLaunchesDotnetDirectlyWithoutAShellHost()
    {
        var goalId = new GoalId("10203040102030401020304010203040");
        var calls = new List<(string FileName, IReadOnlyList<string> Arguments)>();
        var verifier = new LocalProcessVerifier((fileName, args, _, _) =>
        {
            calls.Add((fileName, args));
            if (fileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new LocalProcessVerifier.CommandResult(127, "", "shell host is unavailable"));
            }

            return Task.FromResult(new LocalProcessVerifier.CommandResult(0, "dotnet direct launch", ""));
        });

        try
        {
            var record = await verifier.RunAsync(
                "dotnet --info",
                "C:\\fake\\dir",
                goalId,
                TaskId.New());

            Assert.Equal(0, record.ExitCode);
            Assert.Equal(2, calls.Count);
            Assert.Equal("dotnet", calls[0].FileName);
            Assert.True(calls[0].Arguments.SequenceEqual(["build-server", "shutdown"]));
            Assert.Equal("dotnet", calls[1].FileName);
            Assert.DoesNotContain("powershell.exe", calls[1].Arguments);
            Assert.DoesNotContain("pwsh", calls[1].Arguments);
            Assert.Contains("--artifacts-path", calls[1].Arguments);
            Assert.Contains("dotnet direct launch", record.StandardOutput);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact(DisplayName = "LocalProcessVerifier_retries_once_on_CS2012_and_returns_passed")]
    public async Task LocalProcessVerifierRetriesOnceOnCs2012AndReturnsPassed()
    {
        var goalId = new GoalId("fedcba98fedcba98fedcba98fedcba98");
        var calls = new List<(string FileName, IReadOnlyList<string> Arguments)>();
        var responses = new Queue<LocalProcessVerifier.CommandResult>([
            new(0, "", ""),
            new(1, "error CS2012: Cannot open 'Core.dll' for writing", ""),
            new(0, "", ""),
            new(0, "Test run succeeded.", "")
        ]);

        var verifier = new LocalProcessVerifier((fileName, args, _, _) =>
        {
            calls.Add((fileName, args));
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
            Assert.Equal("dotnet", calls[0].FileName);
            Assert.True(calls[0].Arguments.SequenceEqual(["build-server", "shutdown"]));
            Assert.Equal("dotnet", calls[2].FileName);
            Assert.True(calls[2].Arguments.SequenceEqual(["build-server", "shutdown"]));
            Assert.Equal("dotnet", calls[1].FileName);
            Assert.Equal("dotnet", calls[3].FileName);
            Assert.DoesNotContain("powershell.exe", calls[1].Arguments);
            Assert.DoesNotContain("pwsh", calls[1].Arguments);
            Assert.Contains("--artifacts-path", calls[1].Arguments);
            Assert.Equal(calls[1].FileName, calls[3].FileName);
            Assert.True(calls[1].Arguments.SequenceEqual(calls[3].Arguments));
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
