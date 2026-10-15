[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsRepairHintWrapper
{
    [Xunit.Fact]
    public void MissingReceiptPrintsSanctionedWorkerBuildCommand()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var assembly = sandbox.CreateManagedAssemblyPlaceholder();
        File.Delete(Path.Combine(Path.GetDirectoryName(assembly)!, ".mcg-build-receipt.txt"));
        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Assert.Equal(24, result.ExitCode);
        var line = Assert.Single(result.Stdout.Split('\n').Where(line => line.StartsWith("Sanctioned build: ", StringComparison.Ordinal)));
        var project = Path.Combine(sandbox.Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        Assert.Equal($"Sanctioned build: & \"{sandbox.Root}/scripts/Invoke-WorkerBuildCheck.ps1\" \"{project}\" -Configuration \"Debug\"", line.TrimEnd('\r'));
        Assert.DoesNotContain("Repair: dotnet build", result.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.ArgumentLog));
        // The real-helper bridge fixture executes this emitted command before its two reuse runs.
    }
}
