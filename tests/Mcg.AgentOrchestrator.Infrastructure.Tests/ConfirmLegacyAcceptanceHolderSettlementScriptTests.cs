using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConfirmLegacyAcceptanceHolderSettlementScriptTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"settle-holder-script-{Guid.NewGuid():N}");
    private string MetadataPath => Path.Combine(_root, "legacy.attempt.json");
    private string ReceiptPath => MetadataPath + ".legacy-holder-settled.json";
    private string EvidencePath => Path.Combine(_root, "tree-evidence.txt");

    [Theory]
    [InlineData("{ throw 'access denied' }")]
    [InlineData("{ $null }")]
    public void UnreadableLivePidWithoutBootProof_RefusesClearly(string reader)
    {
        WriteMetadata();

        var result = Run(Started.AddDays(-1), reader);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("holder PID is owned by a process whose start time cannot be read",
            result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("null-valued", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ReceiptPath));
    }

    [Fact]
    public void BeforeBootActivity_SettlesWithoutReadingTheLivePidStartTime()
    {
        WriteMetadata();

        var result = Run(Boot, "{ throw 'access denied' }");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(File.Exists(ReceiptPath));
        using var receipt = JsonDocument.Parse(File.ReadAllText(ReceiptPath));
        Assert.Equal("operator-confirmed-tree-exit", receipt.RootElement.GetProperty("Transition").GetString());
        var evidence = "holder activity predates host boot " + Boot.ToString("O", CultureInfo.InvariantCulture);
        Assert.Contains(evidence, receipt.RootElement.GetProperty("Evidence").GetString()!, StringComparison.Ordinal);
        Assert.Contains("evidence=", result.Output, StringComparison.Ordinal);
        Assert.Contains(evidence, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootBeforeOrEqualToStart_RetainsReadableLivePidRefusal(bool equal)
    {
        WriteMetadata();

        var result = Run(equal ? Started : Started.AddDays(-1));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Live PID with incomplete recorded identity cannot be recovered.",
            result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(ReceiptPath));
    }

    [Theory]
    [InlineData("HeartbeatPath")]
    [InlineData("StdoutPath")]
    [InlineData("StderrPath")]
    [InlineData("ExitCodePath")]
    [InlineData("ResultPath")]
    public void ArtifactAfterBoot_RetainsLivePidRefusal(string field)
    {
        WriteMetadata();
        File.SetLastWriteTimeUtc(ArtifactPath(field), Boot.AddDays(1).UtcDateTime);

        var result = Run(Boot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Live PID with incomplete recorded identity cannot be recovered.",
            result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(ReceiptPath));
    }

    [Fact]
    public void RecordedHeartbeatAfterBoot_PreventsBootProof()
    {
        WriteMetadata(lastHeartbeat: Boot.AddDays(1));

        var result = Run(Boot, "{ throw 'access denied' }");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("holder PID is owned by a process whose start time cannot be read",
            result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(ReceiptPath));
    }

    private string ArtifactPath(string field) => Path.Combine(_root, field + ".txt");

    private void WriteMetadata(DateTimeOffset? lastHeartbeat = null)
    {
        Directory.CreateDirectory(_root);
        // No reconciliation observation: it could independently prove PID reuse and mask a broken boot rule.
        var metadata = new Dictionary<string, object?>
        {
            ["AttemptId"] = "legacy", ["Kind"] = "cohort", ["IdentityValue"] = "candidate",
            ["MetadataPath"] = MetadataPath, ["OwnerProcessId"] = Environment.ProcessId,
            ["StartedAt"] = Started, ["Outcome"] = "Running", ["HolderContractVersion"] = 0
        };
        if (lastHeartbeat is not null) metadata["LastHeartbeatAt"] = lastHeartbeat;
        foreach (var field in new[] { "HeartbeatPath", "StdoutPath", "StderrPath", "ExitCodePath", "ResultPath" })
        {
            var path = ArtifactPath(field);
            metadata[field] = path;
            File.WriteAllText(path, "holder evidence");
            File.SetLastWriteTimeUtc(path, Started.AddDays(1).UtcDateTime);
        }
        File.WriteAllText(MetadataPath, JsonSerializer.Serialize(metadata));
        File.WriteAllText(EvidencePath, "Operator inspected the entire predecessor tree.");
    }

    private (int ExitCode, string Output) Run(DateTimeOffset boot, string? reader = null)
    {
        var script = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "scripts",
            "Confirm-LegacyAcceptanceHolderSettlement.ps1");
        var command = $"& {Quote(script)} -MetadataPath {Quote(MetadataPath)} -EvidencePath {Quote(EvidencePath)} " +
            $"-ConfirmEntireTreeSettled -HostBootTime {Quote(boot.ToString("O", CultureInfo.InvariantCulture))}";
        if (reader is not null) command += " -ProcessStartTimeReader " + reader;
        var startInfo = new ProcessStartInfo
        {
            FileName = TestPowerShell.Executable,
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        var path = Environment.GetEnvironmentVariable("PATH");
        var systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT");
        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(path)) startInfo.Environment["PATH"] = path;
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            startInfo.Environment["SYSTEMROOT"] = systemRoot;
            startInfo.Environment["WINDIR"] = systemRoot;
        }
        startInfo.Environment["TEMP"] = Path.GetTempPath();
        startInfo.Environment["TMP"] = Path.GetTempPath();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch settlement script fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        ScriptFixtureProcessRunner.AssertExited(process, "Settlement script did not exit within its hang failsafe.");
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
