using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckOutputBoundsTests
{
    [Xunit.Fact(DisplayName = "WorkerBuildCheck_warning_heavy_success_bounds_model_visible_output")]
    public async Task WorkerBuildCheckWarningHeavySuccessBoundsModelVisibleOutput()
    {
        var result = await RunFixtureAsync("Warnings");

        Assert.Equal(0, result.ExitCode);
        var receipt = Regex.Match(
            result.Stdout,
            @"raw_chars=(?<raw>\d+) helper_chars=(?<helper>\d+) raw_exit=(?<rawExit>\d+) helper_exit=(?<helperExit>\d+) raw_errors=(?<rawErrors>\d+) helper_errors=(?<helperErrors>\d+)",
            RegexOptions.CultureInvariant);
        Assert.True(receipt.Success, result.Combined);
        Assert.True(int.Parse(receipt.Groups["raw"].Value) >= 256_000, result.Combined);
        Assert.True(int.Parse(receipt.Groups["helper"].Value) < 2_048, result.Combined);
        Assert.Equal(receipt.Groups["rawExit"].Value, receipt.Groups["helperExit"].Value);
        Assert.Equal(receipt.Groups["rawErrors"].Value, receipt.Groups["helperErrors"].Value);
        Assert.Equal("0", receipt.Groups["helperErrors"].Value);
    }

    [Xunit.Theory(DisplayName = "WorkerBuildCheck_preserves_bounded_failure_and_multi_project_contracts")]
    [Xunit.InlineData("CompilerFailure")]
    [Xunit.InlineData("NonDiagnosticFailure")]
    [Xunit.InlineData("MultipleProjects")]
    [Xunit.InlineData("LogUnavailable")]
    public async Task WorkerBuildCheckPreservesBoundedFailureAndMultiProjectContracts(string scenario)
    {
        var result = await RunFixtureAsync(scenario);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"PASS fixture: scenario={scenario}", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerBuildDiagnostic_uses_argument_list_closed_stdin_and_durable_summary")]
    public void WorkerBuildDiagnosticUsesArgumentListClosedStdinAndDurableSummary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var helper = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "Invoke-WorkerBuildCheck.ps1"));
        var diagnostic = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "Invoke-WorkerBuildDiagnostic.ps1"));

        Assert.Contains("-clp:ErrorsOnly", helper, StringComparison.Ordinal);
        Assert.Contains("-tl:off", helper, StringComparison.Ordinal);
        Assert.Contains("\"-fl\"", helper, StringComparison.Ordinal);
        Assert.Contains("-flp:LogFile=$logPath;Verbosity=normal;Encoding=UTF-8;Append=false", helper, StringComparison.Ordinal);
        Assert.Contains("complete-log:", helper, StringComparison.Ordinal);
        Assert.Contains("$startInfo.ArgumentList.Add($argument)", diagnostic, StringComparison.Ordinal);
        Assert.Contains("$startInfo.RedirectStandardInput = $true", diagnostic, StringComparison.Ordinal);
        Assert.Contains("$process.StandardInput.Close()", diagnostic, StringComparison.Ordinal);
        Assert.Contains("ReadToEndAsync()", diagnostic, StringComparison.Ordinal);
        Assert.Contains("(not worker build evidence)", diagnostic, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunFixtureAsync(string scenario)
    {
        // Hang-only bound exceeds the Warnings scenario's two 120-second child guards.
        const int FixtureHangBoundSeconds = 300;
        var repositoryRoot = FindRepositoryRoot();
        var fixturePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures",
            "WorkerBuildCheckOutputBounds",
            "Invoke-Fixture.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", fixturePath, "-Scenario", scenario
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var capture = await Task.Run(() => TestChildProcessCapture.Run(
            startInfo, hangBound: TimeSpan.FromSeconds(FixtureHangBoundSeconds)));
        return new ProcessResult(capture.ExitCode, capture.Stdout, capture.Stderr);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Combined => Stdout + Environment.NewLine + Stderr;
    }
}
