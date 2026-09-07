using System.Security.Cryptography;
using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpNoBuildReceiptIdentityTests
{
    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_receipt_for_a_different_configuration_before_launch")]
    public void MtpNoBuildRejectsReceiptForDifferentConfigurationBeforeLaunch()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var assembly = sandbox.CreateManagedAssemblyPlaceholder();
        var receiptPath = Path.Combine(Path.GetDirectoryName(assembly)!, ".mcg-build-receipt.txt");
        File.WriteAllText(
            receiptPath,
            File.ReadAllText(receiptPath).Replace("K\tconfiguration\tDebug", "K\tconfiguration\tRelease", StringComparison.Ordinal));

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("receipt key 'configuration' does not match: recorded='Release' expected='Debug'", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "The runner must not launch a receipt for a different configuration.");
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_a_copied_receipt_from_a_sibling_worktree_before_launch")]
    public void MtpNoBuildRejectsCopiedReceiptFromSiblingWorktreeBeforeLaunch()
    {
        using var source = MtpTestRunnerScriptTests.ScriptSandbox.Create("success", rootNamePrefix: "worktree-a");
        using var candidate = MtpTestRunnerScriptTests.ScriptSandbox.Create("success", rootNamePrefix: "worktree-b");
        var sourceAssembly = source.CreateManagedAssemblyPlaceholder();
        var sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
        var candidateAssembly = candidate.CreateManagedAssemblyPlaceholder();
        var candidateDirectory = Path.GetDirectoryName(candidateAssembly)!;
        Directory.Delete(candidateDirectory, recursive: true);
        CopyDirectory(sourceDirectory, candidateDirectory);

        var result = candidate.RunPartition("GoalWorktree", dotnetPath: candidate.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("receipt key 'repositoryRoot' does not match", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains(source.Root, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(candidate.Root, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.False(File.Exists(candidate.ArgumentLog), "The runner must not launch a receipt copied from another worktree.");
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_stale_output_with_a_newer_timestamp_after_source_change")]
    public void MtpNoBuildRejectsStaleOutputWithNewerTimestampAfterSourceChange()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var assembly = sandbox.CreateManagedAssemblyPlaceholder();
        var outputDirectory = Path.GetDirectoryName(assembly)!;
        File.AppendAllText(Path.Combine(outputDirectory, "receipt-source.cs"), "// changed after build");
        File.SetLastWriteTimeUtc(assembly, DateTime.UtcNow.AddDays(1));
        File.SetLastWriteTimeUtc(Path.ChangeExtension(assembly, ".pdb"), DateTime.UtcNow.AddDays(1));

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("receipt source checksum mismatch", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "Timestamp freshness must not make stale output eligible for launch.");
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_reuses_an_unchanged_sealed_output_across_separate_runs")]
    public void MtpNoBuildReusesUnchangedSealedOutputAcrossSeparateRuns()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var assembly = sandbox.CreateManagedAssemblyPlaceholder();
        var buildIdentity = Sha256(assembly);

        var first = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);
        var second = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(0, first.ExitCode);
        Xunit.Assert.Equal(0, second.ExitCode);
        Xunit.Assert.Equal(buildIdentity, Sha256(assembly));
        var firstEvidence = TerminalSummary(first).GetProperty("retainedEvidenceDirectory").GetString();
        var secondEvidence = TerminalSummary(second).GetProperty("retainedEvidenceDirectory").GetString();
        Xunit.Assert.False(string.IsNullOrWhiteSpace(firstEvidence));
        Xunit.Assert.False(string.IsNullOrWhiteSpace(secondEvidence));
        Xunit.Assert.NotEqual(firstEvidence, secondEvidence);
        Xunit.Assert.True(File.Exists(Path.Combine(firstEvidence!, "run-identity.json")));
        Xunit.Assert.True(File.Exists(Path.Combine(secondEvidence!, "run-identity.json")));
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_launches_a_verified_evaluated_standard_output")]
    public void MtpNoBuildLaunchesVerifiedEvaluatedStandardOutput()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var declaredAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        var declaredDirectory = Path.GetDirectoryName(declaredAssembly)!;
        var sourcePath = Path.Combine(declaredDirectory, "receipt-source.cs");
        var sourceText = File.ReadAllText(sourcePath);
        var evaluatedDirectory = Path.Combine(
            sandbox.Root,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "bin",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(Path.GetDirectoryName(evaluatedDirectory)!);
        Directory.Move(declaredDirectory, evaluatedDirectory);
        Directory.CreateDirectory(declaredDirectory);
        File.WriteAllText(sourcePath, sourceText);
        var receiptPath = Path.Combine(evaluatedDirectory, ".mcg-build-receipt.txt");
        var receipt = File.ReadAllText(receiptPath)
            .Replace(declaredDirectory + Path.DirectorySeparatorChar, evaluatedDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .Replace(
                $"S\tsource\t{Path.Combine(evaluatedDirectory, "receipt-source.cs")}\t{Sha256(sourcePath)}",
                $"S\tsource\t{sourcePath}\t{Sha256(sourcePath)}",
                StringComparison.Ordinal);
        File.WriteAllText(receiptPath, receipt);

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains("NO-BUILD BUILD RECEIPT SELECTED - evaluated standard output", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.True(File.Exists(sandbox.ArgumentLog), "The verified evaluated standard output must reach the runner.");
    }

    private static JsonElement TerminalSummary(MtpTestRunnerScriptTests.ProcessResult result)
    {
        var summary = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("MTP_TERMINAL_SUMMARY ", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(summary["MTP_TERMINAL_SUMMARY ".Length..]);
        return document.RootElement.Clone();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }
    }
}
