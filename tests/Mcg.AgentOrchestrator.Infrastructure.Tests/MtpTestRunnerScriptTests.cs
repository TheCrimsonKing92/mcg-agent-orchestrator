using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTests
{
    [Xunit.Fact(DisplayName = "MTP_script_filter_translation_and_filename_bounding_match_gate_conventions")]
    public void MtpScriptFilterTranslationAndFilenameBoundingMatchGateConventions()
    {
        var module = Path.Combine(RepositoryRoot(), "scripts", "MtpTestRunner.psm1");
        var command = $"Import-Module '{module.Replace("'", "''")}' -Force; " +
            "$result = [ordered]@{ args = @(ConvertTo-MtpFilterArguments 'FullyQualifiedName~GoalWorktreeTests&FullyQualifiedName!~Cleanup&Category!=HostIntegration'); name = Get-MtpBoundedFileName ('x' * 400) }; " +
            "$result | ConvertTo-Json -Compress";

        var result = RunPowerShellCommand(RepositoryRoot(), command);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout.Trim());
        var arguments = document.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
        Xunit.Assert.Equal(
            ["--filter-class", "*GoalWorktreeTests*", "--filter-not-class", "*Cleanup*", "--filter-not-trait", "Category=HostIntegration"],
            arguments);
        var fileName = document.RootElement.GetProperty("name").GetString();
        Xunit.Assert.NotNull(fileName);
        Xunit.Assert.True(fileName!.Length <= 200, fileName);
        Xunit.Assert.Matches(@"-[0-9a-f]{16}\.trx$", fileName);
    }

    [Xunit.Fact(DisplayName = "MTP_partition_validation_comes_from_manifest_and_precedes_build")]
    public void MtpPartitionValidationComesFromManifestAndPrecedesBuild()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var buildMarker = Path.Combine(sandbox.Root, "build-started.txt");
        var fakeDotnet = sandbox.CreateBuildStub(exitCode: 0, buildMarker);

        var result = sandbox.RunPartition("Typo", noBuild: false, dotnetPath: fakeDotnet);

        Xunit.Assert.Equal(21, result.ExitCode);
        Xunit.Assert.Contains("UNKNOWN PARTITION 'Typo'", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("GoalWorktree", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(buildMarker));
    }

    [Xunit.Fact(DisplayName = "MTP_partition_runner_streams_output_and_passes_manifest_filter_arguments")]
    public void MtpPartitionRunnerStreamsOutputAndPassesManifestFilterArguments()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var result = sandbox.RunPartition("GoalWorktree");

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("stub stdout", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("stub stderr", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("TRX:", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("PARTITION GREEN", result.Stdout, StringComparison.Ordinal);
        var arguments = File.ReadAllLines(sandbox.ArgumentLog);
        Xunit.Assert.Contains("--no-ansi", arguments);
        Xunit.Assert.Contains("--report-trx", arguments);
        Xunit.Assert.Contains("--filter-class", arguments);
        Xunit.Assert.Contains("*GoalWorktreeTests*", arguments);
        Xunit.Assert.DoesNotContain(arguments, argument => argument.Contains("testhost", StringComparison.OrdinalIgnoreCase));
        var trxPath = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("TRX: ", StringComparison.Ordinal))[5..];
        Xunit.Assert.True(trxPath.Length <= 240, trxPath);
    }

    [Xunit.Fact(DisplayName = "MTP_local_partitions_preserve_CLI_help_and_dashboard_rendering_coverage")]
    public void MtpLocalPartitionsPreserveCliHelpAndDashboardRenderingCoverage()
    {
        var root = RepositoryRoot();
        var module = Path.Combine(root, "scripts", "MtpTestRunner.psm1");
        var manifest = Path.Combine(root, "config", "acceptance-manifest.json");
        var command = $"Import-Module '{module.Replace("'", "''")}' -Force; " +
            $"$manifest = Read-MtpTestManifest '{manifest.Replace("'", "''")}'; $partitions = @(Get-MtpLocalPartitions $manifest); " +
            "$result = [ordered]@{ cli = @(($partitions | Where-Object Name -eq 'Cli').Filters); dashboard = @(($partitions | Where-Object Name -eq 'Dashboard').Filters) }; " +
            "$result | ConvertTo-Json -Compress";

        var result = RunPowerShellCommand(root, command);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout.Trim());
        var cliFilters = document.RootElement.GetProperty("cli").EnumerateArray().Select(item => item.GetString()).ToArray();
        var dashboardFilters = document.RootElement.GetProperty("dashboard").EnumerateArray().Select(item => item.GetString()).ToArray();
        Xunit.Assert.Contains("FullyQualifiedName~CliHelpTests", cliFilters);
        Xunit.Assert.Contains("FullyQualifiedName~DashboardRenderingTests", dashboardFilters);
        Xunit.Assert.Contains("FullyQualifiedName~DashboardHostTests", dashboardFilters);
    }

    [Xunit.Theory]
    [Xunit.InlineData("no-trx", 7, "RUNNER/TOOLING FAILURE", "exited 7 without producing TRX")]
    [Xunit.InlineData("zero", 27, "ZERO TESTS", "filter matched no tests")]
    [Xunit.InlineData("failed", 3, "TEST FAILURES", "TRX:")]
    public void MtpPartitionRunnerSelectsDistinctFailureDiagnosis(
        string behavior,
        int expectedExitCode,
        string expectedDiagnosis,
        string expectedDetail)
    {
        using var sandbox = ScriptSandbox.Create(behavior);

        var result = sandbox.RunPartition("GoalWorktree");

        Assert.True(result.ExitCode == expectedExitCode, result.Stdout + result.Stderr);
        Assert.Contains(expectedDiagnosis, result.Stdout, StringComparison.Ordinal);
        Assert.Contains(expectedDetail, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("NO TRX", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retained diagnostic directory:", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "MTP_partition_build_failure_is_loud_and_never_launches_stale_apphost")]
    public void MtpPartitionBuildFailureIsLoudAndNeverLaunchesStaleApphost()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var buildMarker = Path.Combine(sandbox.Root, "build-started.txt");
        var fakeDotnet = sandbox.CreateBuildStub(exitCode: 9, buildMarker);

        var result = sandbox.RunPartition("GoalWorktree", noBuild: false, dotnetPath: fakeDotnet);

        Xunit.Assert.Equal(23, result.ExitCode);
        Xunit.Assert.Contains("compiler diagnostic from stub", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("BUILD FAILURE", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("MTP apphost was not launched", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.True(File.Exists(buildMarker));
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog));
        Xunit.Assert.DoesNotContain("NO TRX", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_missing_apphost_reports_path_and_build_command")]
    public void MtpNoBuildMissingApphostReportsPathAndBuildCommand()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var result = sandbox.RunPartition("GoalWorktree", runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("MISSING APPHOST", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains(
            Path.Combine("bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Debug", "Mcg.AgentOrchestrator.Infrastructure.Tests.exe"),
            result.Stdout,
            StringComparison.Ordinal);
        Xunit.Assert.Contains("dotnet build", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("NO TRX", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "MTP_medium_integrity_results_override_fails_before_runner_launch")]
    public void MtpMediumIntegrityResultsOverrideFailsBeforeRunnerLaunch()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var mediumResults = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "mcg-mtp-medium-tests", Guid.NewGuid().ToString("n"));

        try
        {
            var result = sandbox.RunPartition("GoalWorktree", resultsRoot: mediumResults);

            Xunit.Assert.True(result.ExitCode == 22, result.Stdout + result.Stderr);
            Xunit.Assert.Contains("RESULTS DIRECTORY FAILURE", result.Stdout, StringComparison.Ordinal);
            Xunit.Assert.Contains("Low-integrity-writable root", result.Stdout, StringComparison.Ordinal);
            Xunit.Assert.Contains("Medium-integrity", result.Stdout, StringComparison.Ordinal);
            Xunit.Assert.False(File.Exists(sandbox.ArgumentLog));
            Xunit.Assert.False(Directory.Exists(mediumResults));
        }
        finally
        {
            if (Directory.Exists(mediumResults))
            {
                Directory.Delete(mediumResults, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "MTP_runner_preserves_child_and_caller_temp_roots_after_clean_run")]
    public void MtpRunnerPreservesChildAndCallerTempRootsAfterCleanRun()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var result = sandbox.RunModuleAndReportEnvironment();

        Xunit.Assert.Equal(0, result.ExitCode);
        var json = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.StartsWith('{'));
        using var document = JsonDocument.Parse(json);
        Xunit.Assert.Contains("stub temp=caller-temp tmp=caller-tmp tmpdir=caller-tmpdir", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Equal("caller-temp", document.RootElement.GetProperty("temp").GetString());
        Xunit.Assert.Equal("caller-tmp", document.RootElement.GetProperty("tmp").GetString());
        Xunit.Assert.Equal("caller-tmpdir", document.RootElement.GetProperty("tmpdir").GetString());
        Xunit.Assert.Equal(
            document.RootElement.GetProperty("expectedLocalAppData").GetString(),
            document.RootElement.GetProperty("localAppData").GetString());
        Xunit.Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Xunit.Fact(DisplayName = "MTP_public_scripts_have_no_VSTest_or_discarded_runner_path")]
    public void MtpPublicScriptsHaveNoVstestOrDiscardedRunnerPath()
    {
        var root = RepositoryRoot();
        var sources = new[]
        {
            File.ReadAllText(Path.Combine(root, "scripts", "Invoke-InfrastructureTestPartition.ps1")),
            File.ReadAllText(Path.Combine(root, "scripts", "Invoke-TestSummary.ps1")),
            File.ReadAllText(Path.Combine(root, "scripts", "MtpTestRunner.psm1"))
        };
        foreach (var source in sources)
        {
            Xunit.Assert.DoesNotContain("dotnet test", source, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.DoesNotContain("--logger", source, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.DoesNotContain("NO TRX OUTPUT", source, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.DoesNotContain("stable-slot-dotnet", source, StringComparison.OrdinalIgnoreCase);
        }
        Xunit.Assert.Contains("MtpTestRunner.psm1", sources[0], StringComparison.Ordinal);
        Xunit.Assert.Contains("MtpTestRunner.psm1", sources[1], StringComparison.Ordinal);
        Xunit.Assert.Contains("& $Executable @($Arguments | Select-Object -Skip 1) 2>&1 | ForEach-Object", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("CLEANUP FAILURE", sources[2], StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("$runDirectory -Recurse -Force -ErrorAction SilentlyContinue", sources[2], StringComparison.Ordinal);
    }

    private static ProcessResult RunPowerShellCommand(string workingDirectory, string command)
    {
        var startInfo = PowerShellStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        return Run(startInfo);
    }

    private static ProcessStartInfo PowerShellStartInfo(string workingDirectory) => new()
    {
        FileName = "powershell.exe",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass" }
    };

    private static ProcessResult Run(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Xunit.Assert.True(process.WaitForExit(30_000), $"{startInfo.FileName} did not exit within 30 seconds.");
        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class ScriptSandbox : IDisposable
    {
        private ScriptSandbox(string root, string resultsRoot, string runnerPath, string argumentLog)
        {
            Root = root;
            ResultsRoot = resultsRoot;
            RunnerPath = runnerPath;
            ArgumentLog = argumentLog;
        }

        public string Root { get; }
        public string ResultsRoot { get; }
        public string RunnerPath { get; }
        public string ArgumentLog { get; }

        public static ScriptSandbox Create(string behavior)
        {
            var root = Path.Combine(Path.GetTempPath(), "mtp-script-tests", Guid.NewGuid().ToString("n"));
            var resultsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "Low", "mcg-tests", "script-tests", Guid.NewGuid().ToString("n"));
            var scripts = Path.Combine(root, "scripts");
            var config = Path.Combine(root, "config");
            var projectDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(resultsRoot);

            foreach (var fileName in new[] { "Invoke-InfrastructureTestPartition.ps1", "MtpTestRunner.psm1" })
            {
                File.Copy(Path.Combine(RepositoryRoot(), "scripts", fileName), Path.Combine(scripts, fileName));
            }
            File.WriteAllText(Path.Combine(projectDirectory, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), """
                {
                  "engine": {
                    "infrastructureTestLanes": [
                      { "name": "Goal lane", "filter": "FullyQualifiedName~GoalWorktreeTests" }
                    ],
                    "localTestPartitions": [
                      { "name": "GoalWorktree", "description": "Goal tests.", "laneNames": [ "Goal lane" ] }
                    ],
                    "mtpInvocations": [
                      {
                        "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                        "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                        "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                        "arguments": [
                          "{executable}", "--no-ansi", "--progress", "off",
                          "--results-directory", "{resultsDirectory}",
                          "--report-trx", "--report-trx-filename", "{trxFileName}"
                        ]
                      }
                    ]
                  }
                }
                """);

            var argumentLog = Path.Combine(root, "runner-arguments.txt");
            var fakeRunnerScript = Path.Combine(scripts, "FakeRunner.ps1");
            var escapedArgumentLog = argumentLog.Replace("'", "''");
            var resultBody = behavior switch
            {
                "success" => TrxBody(total: 1, passed: 1, failed: 0),
                "zero" => TrxBody(total: 0, passed: 0, failed: 0),
                "failed" => TrxBody(total: 1, passed: 0, failed: 1),
                "no-trx" => string.Empty,
                _ => throw new ArgumentOutOfRangeException(nameof(behavior))
            };
            var exitCode = behavior switch
            {
                "failed" => 3,
                "no-trx" => 7,
                _ => 0
            };
            var escapedTrx = resultBody.Replace("'", "''");
            File.WriteAllText(fakeRunnerScript, $$"""
                param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
                $Arguments | Set-Content -LiteralPath '{{escapedArgumentLog}}'
                Write-Output 'stub stdout'
                Write-Output "stub temp=$env:TEMP tmp=$env:TMP tmpdir=$env:TMPDIR"
                [Console]::Error.WriteLine('stub stderr')
                $resultsIndex = [Array]::IndexOf($Arguments, '--results-directory')
                $fileIndex = [Array]::IndexOf($Arguments, '--report-trx-filename')
                if ('{{behavior}}' -ne 'no-trx') {
                    $trxPath = Join-Path $Arguments[$resultsIndex + 1] $Arguments[$fileIndex + 1]
                    '{{escapedTrx}}' | Set-Content -LiteralPath $trxPath -Encoding UTF8
                }
                exit {{exitCode}}
                """);
            var runnerPath = Path.Combine(scripts, "FakeRunner.cmd");
            File.WriteAllText(runnerPath, """
                @echo off
                powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FakeRunner.ps1" %*
                exit /b %ERRORLEVEL%
                """);
            return new ScriptSandbox(root, resultsRoot, runnerPath, argumentLog);
        }

        public string CreateBuildStub(int exitCode, string markerPath)
        {
            var path = Path.Combine(Root, "fake-dotnet.cmd");
            File.WriteAllText(path, $"@echo off{Environment.NewLine}echo started>\"{markerPath}\"{Environment.NewLine}echo compiler diagnostic from stub{Environment.NewLine}exit /b {exitCode}{Environment.NewLine}");
            return path;
        }

        public ProcessResult RunPartition(
            string partition,
            bool noBuild = true,
            string? dotnetPath = null,
            bool runnerOverride = true,
            string? resultsRoot = null)
        {
            var startInfo = PowerShellStartInfo(Root);
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(Root, "scripts", "Invoke-InfrastructureTestPartition.ps1"));
            startInfo.ArgumentList.Add("-Partition");
            startInfo.ArgumentList.Add(partition);
            startInfo.ArgumentList.Add("-ResultsRoot");
            startInfo.ArgumentList.Add(resultsRoot ?? ResultsRoot);
            if (noBuild)
            {
                startInfo.ArgumentList.Add("-NoBuild");
            }
            if (runnerOverride)
            {
                startInfo.ArgumentList.Add("-RunnerPath");
                startInfo.ArgumentList.Add(RunnerPath);
            }
            if (dotnetPath is not null)
            {
                startInfo.ArgumentList.Add("-DotnetPath");
                startInfo.ArgumentList.Add(dotnetPath);
            }
            return Run(startInfo);
        }

        public ProcessResult RunModuleAndReportEnvironment()
        {
            var module = Path.Combine(Root, "scripts", "MtpTestRunner.psm1").Replace("'", "''");
            var manifest = Path.Combine(Root, "config", "acceptance-manifest.json").Replace("'", "''");
            var root = Root.Replace("'", "''");
            var resultsRoot = ResultsRoot.Replace("'", "''");
            var runner = RunnerPath.Replace("'", "''");
            var command =
                $"Import-Module '{module}' -Force; " +
                "$env:TEMP = 'caller-temp'; $env:TMP = 'caller-tmp'; $env:TMPDIR = 'caller-tmpdir'; $beforeLocalAppData = $env:LOCALAPPDATA; " +
                $"$manifest = Read-MtpTestManifest '{manifest}'; " +
                $"$run = Invoke-MtpTestRun -RepositoryRoot '{root}' -Manifest $manifest " +
                "-Target 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj' " +
                $"-Filters 'FullyQualifiedName~GoalWorktreeTests' -RunLabel 'environment' -NoBuild -ResultsRoot '{resultsRoot}' -RunnerPath '{runner}'; " +
                "$result = [ordered]@{ exitCode = $run.ExitCode; temp = $env:TEMP; tmp = $env:TMP; tmpdir = $env:TMPDIR; localAppData = $env:LOCALAPPDATA; expectedLocalAppData = $beforeLocalAppData }; " +
                "$result | ConvertTo-Json -Compress";
            return RunPowerShellCommand(Root, command);
        }

        public void Dispose()
        {
            TryDelete(Root);
            TryDelete(ResultsRoot);
        }

        private static string TrxBody(int total, int passed, int failed) => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun>
              <ResultSummary outcome="Completed">
                <Counters total="{total}" executed="{total}" passed="{passed}" failed="{failed}" notExecuted="0" />
              </ResultSummary>
              <Results>{(failed > 0 ? "<UnitTestResult testName=\"stub failure\" outcome=\"Failed\"><Output><ErrorInfo><Message>expected failure</Message></ErrorInfo></Output></UnitTestResult>" : string.Empty)}</Results>
            </TestRun>
            """;

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best-effort fixture cleanup; assertions retain the original failure signal.
            }
        }
    }
}
