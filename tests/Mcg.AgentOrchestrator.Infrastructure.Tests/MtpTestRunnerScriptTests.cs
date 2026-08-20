using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTests
{
    [Xunit.Fact(DisplayName = "Repository_test_projects_opt_into_native_MTP_dotnet_test")]
    public void RepositoryTestProjectsOptIntoNativeMtpDotnetTest()
    {
        var root = RepositoryRoot();
        using var globalJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        Xunit.Assert.Equal(
            "Microsoft.Testing.Platform",
            globalJson.RootElement.GetProperty("test").GetProperty("runner").GetString());

        var properties = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        Xunit.Assert.Empty(properties.Descendants("TestingPlatformDotnetTestSupport"));
        Xunit.Assert.Equal(
            "$(McgIsolatedArtifactsPath)\\bin\\$(MSBuildProjectName)\\",
            properties.Descendants("BaseOutputPath").Single().Value);
        Xunit.Assert.Equal(
            "$(McgIsolatedArtifactsPath)\\obj\\$(MSBuildProjectName)\\",
            properties.Descendants("BaseIntermediateOutputPath").Single().Value);
        Xunit.Assert.Equal("false", properties.Descendants("AppendTargetFrameworkToOutputPath").Single().Value);
        Xunit.Assert.Contains(
            "$(MSBuildProjectDirectory)\\obj\\**",
            properties.Descendants("DefaultItemExcludes").Last().Value,
            StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "$(MSBuildProjectDirectory)\\bin\\**",
            properties.Descendants("DefaultItemExcludes").Last().Value,
            StringComparison.Ordinal);

        string[] testProjects =
        [
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj"
        ];
        foreach (var project in testProjects)
        {
            var document = XDocument.Load(Path.Combine(root, project));
            Xunit.Assert.Equal("true", document.Descendants("IsTestProject").Single().Value, ignoreCase: true);
            Xunit.Assert.Equal("Exe", document.Descendants("OutputType").Single().Value, ignoreCase: true);
            Xunit.Assert.Equal(
                "true",
                document.Descendants("UseMicrosoftTestingPlatformRunner").Single().Value,
                ignoreCase: true);
        }
    }

    [Xunit.Theory(DisplayName = "Native_MTP_dotnet_test_runs_every_repository_test_project_in_one_step")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "RepositoryChangeClassifierTests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "ProcessStartInfoSourceGuardTests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj", "ProviderDefaultTests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj", "CliArgumentNormalizationTests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj", "DashboardValidationHarnessTests")]
    public void NativeMtpDotnetTestRunsEveryRepositoryTestProjectInOneStep(string project, string testClass)
    {
        var root = RepositoryRoot();
        var goalId = new GoalId(Guid.NewGuid().ToString("N"));
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "mtp-one-step-contract");
        var acquisition = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(
            environment,
            timeout: TimeSpan.FromMinutes(5));
        var acquired = Xunit.Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisition);
        var lease = acquired.Lease;
        var artifactsPath = lease.Environment.ArtifactsPath;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var nugetHttpCache = Path.Combine(lease.Environment.RootPath, "nuget-http-cache");
            Directory.CreateDirectory(nugetHttpCache);
            startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = nugetHttpCache;
            startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
            foreach (var argument in new[]
            {
                "test",
                "--project",
                project,
                $"--property:McgIsolatedArtifactsPath={artifactsPath}",
                "--property:BuildInParallel=false",
                "--",
                "--filter-class",
                $"*{testClass}*",
                "--minimum-expected-tests",
                "1",
                "--no-ansi",
                "--progress",
                "off"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var result = Run(startInfo, timeout: TimeSpan.FromMinutes(15));

            Xunit.Assert.True(
                result.ExitCode == 0,
                $"dotnet test --project {project} exited {result.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{result.Stderr}");
        }
        finally
        {
            lease.Dispose();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

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
        var terminal = TerminalSummary(result);
        Xunit.Assert.Equal("failed", terminal.GetProperty("outcome").GetString());
        Xunit.Assert.Equal(21, terminal.GetProperty("exitCode").GetInt32());
    }

    [Xunit.Fact(DisplayName = "MTP_partition_runner_streams_output_and_passes_manifest_filter_arguments")]
    public void MtpPartitionRunnerStreamsOutputAndPassesManifestFilterArguments()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var result = sandbox.RunPartition("GoalWorktree", testHostTimeoutSeconds: 17);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("stub stdout", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("stub stderr", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("TRX:", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("PARTITION GREEN", result.Stdout, StringComparison.Ordinal);
        var arguments = File.ReadAllLines(sandbox.ArgumentLog);
        Xunit.Assert.Contains("--no-ansi", arguments);
        Xunit.Assert.Contains("--report-trx", arguments);
        Xunit.Assert.Contains("--timeout", arguments);
        Xunit.Assert.Contains("17s", arguments);
        Xunit.Assert.Contains("--filter-class", arguments);
        Xunit.Assert.Contains("*GoalWorktreeTests*", arguments);
        Xunit.Assert.DoesNotContain(arguments, argument => argument.Contains("testhost", StringComparison.OrdinalIgnoreCase));
        var trxPath = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("TRX: ", StringComparison.Ordinal))[5..];
        Xunit.Assert.True(trxPath.Length <= 240, trxPath);
        var terminal = TerminalSummary(result);
        Xunit.Assert.Equal("completed", terminal.GetProperty("outcome").GetString());
        Xunit.Assert.Equal(0, terminal.GetProperty("exitCode").GetInt32());
        Xunit.Assert.True(terminal.GetProperty("exitConfirmed").GetBoolean());
        Xunit.Assert.False(terminal.GetProperty("artifactsRetained").GetBoolean());
    }

    [Xunit.Fact(DisplayName = "MTP_summary_partition_defaults_to_the_infrastructure_test_project")]
    public void MtpSummaryPartitionDefaultsToTheInfrastructureTestProject()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var result = sandbox.RunSummaryPartition("GoalWorktree");

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("stub stdout", result.Stdout, StringComparison.Ordinal);
        var arguments = File.ReadAllLines(sandbox.ArgumentLog);
        Xunit.Assert.Contains("*GoalWorktreeTests*", arguments);
    }

    [Xunit.Fact(DisplayName = "MTP_local_partitions_preserve_CLI_help_and_dashboard_rendering_coverage")]
    public void MtpLocalPartitionsPreserveCliHelpAndDashboardRenderingCoverage()
    {
        var root = RepositoryRoot();
        var module = Path.Combine(root, "scripts", "MtpTestRunner.psm1");
        var manifest = Path.Combine(root, "config", "acceptance-manifest.json");
        var command = $"Import-Module '{module.Replace("'", "''")}' -Force; " +
            $"$manifest = Read-MtpTestManifest '{manifest.Replace("'", "''")}'; $partitions = @(Get-MtpLocalPartitions $manifest); " +
            "$result = [ordered]@{ cli = @(($partitions | Where-Object Name -eq 'Cli').Filters); names = @($partitions.Name) }; " +
            "$result | ConvertTo-Json -Compress";

        var result = RunPowerShellCommand(root, command);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout.Trim());
        var cliFilters = document.RootElement.GetProperty("cli").EnumerateArray().Select(item => item.GetString()).ToArray();
        Xunit.Assert.Contains("FullyQualifiedName~CliHelpTests", cliFilters);
        var partitionNames = document.RootElement.GetProperty("names").EnumerateArray().Select(item => item.GetString()).ToArray();
        Xunit.Assert.DoesNotContain("Dashboard", partitionNames);
    }

    [Xunit.Theory]
    [Xunit.InlineData("no-trx", 7, "COMPLETED WITH MISSING TRX", "exited 7 without producing TRX", "completed-with-missing-trx")]
    [Xunit.InlineData("zero", 27, "ZERO TESTS", "filter matched no tests", "failed")]
    [Xunit.InlineData("failed", 3, "TEST FAILURES", "TRX:", "failed")]
    public void MtpPartitionRunnerSelectsDistinctFailureDiagnosis(
        string behavior,
        int expectedExitCode,
        string expectedDiagnosis,
        string expectedDetail,
        string expectedOutcome)
    {
        using var sandbox = ScriptSandbox.Create(behavior);

        var result = sandbox.RunPartition("GoalWorktree");

        Assert.True(result.ExitCode == expectedExitCode, result.Stdout + result.Stderr);
        Assert.Contains(expectedDiagnosis, result.Stdout, StringComparison.Ordinal);
        Assert.Contains(expectedDetail, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("NO TRX", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retained diagnostic directory:", result.Stdout, StringComparison.Ordinal);
        var terminal = TerminalSummary(result);
        Assert.Equal(expectedOutcome, terminal.GetProperty("outcome").GetString());
        Assert.True(terminal.GetProperty("artifactsRetained").GetBoolean());
        Assert.True(terminal.GetProperty("exitConfirmed").GetBoolean());
    }

    [Xunit.Fact]
    public void MtpTimeoutTerminatesOwnedTreeAndPrintsTypedReceipt()
    {
        using var sandbox = ScriptSandbox.Create("hang");

        var result = sandbox.RunPartition("GoalWorktree", testHostTimeoutSeconds: 1);

        Assert.True(result.ExitCode == 29, result.Stdout + result.Stderr);
        Assert.True(File.Exists(sandbox.ReadyPath), result.Stdout + result.Stderr);
        var terminal = TerminalSummary(result);
        Assert.Equal("timed-out", terminal.GetProperty("outcome").GetString());
        Assert.Equal(29, terminal.GetProperty("exitCode").GetInt32());
        Assert.True(terminal.GetProperty("ownedProcessId").GetInt32() > 0);
        Assert.True(terminal.GetProperty("exitConfirmed").GetBoolean());
        Assert.True(terminal.GetProperty("artifactsRetained").GetBoolean());
        Assert.Empty(terminal.GetProperty("trxPaths").EnumerateArray());
        Assert.Single(terminal.GetProperty("expectedTrxPaths").EnumerateArray());
        var runnerLog = Assert.Single(terminal.GetProperty("runnerLogPaths").EnumerateArray()).GetString();
        Assert.NotNull(runnerLog);
        Assert.True(File.Exists(runnerLog), runnerLog);
        Assert.Contains("hang descendant ready", File.ReadAllText(runnerLog!), StringComparison.Ordinal);
        using var lockProbe = new FileStream(sandbox.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Xunit.Fact]
    public void MtpTimeoutRetainsOwnershipAfterRootExitsDuringGrace()
    {
        using var sandbox = ScriptSandbox.Create("root-exits-descendant-locks");

        var result = sandbox.RunPartitionAfterStdoutGate(
            "GoalWorktree",
            "TEST HOST TIMEOUT - owned PID",
            sandbox.ReleasePath,
            testHostTimeoutSeconds: 1);

        Assert.True(result.ExitCode == 29, result.Stdout + result.Stderr);
        Assert.True(File.Exists(sandbox.ReadyPath), result.Stdout + result.Stderr);
        Assert.True(File.Exists(sandbox.ReleasePath), result.Stdout + result.Stderr);
        Assert.Contains("root released after wrapper timeout", result.Stdout, StringComparison.Ordinal);
        var terminal = TerminalSummary(result);
        Assert.Equal("timed-out", terminal.GetProperty("outcome").GetString());
        Assert.True(terminal.GetProperty("exitConfirmed").GetBoolean());
        using var lockProbe = new FileStream(sandbox.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Xunit.Fact]
    public void MtpTimedOutUnconfirmedResultCarriesCleanupDiagnostic()
    {
        var module = Path.Combine(RepositoryRoot(), "scripts", "MtpTestRunner.psm1");
        var command = $"Import-Module '{module.Replace("'", "''")}' -Force; " +
            "$result = New-MtpTerminalResult -Outcome timed-out -ExitCode 29 -ExitConfirmed $false; " +
            "Write-MtpTerminalSummary -Result $result";

        var result = RunPowerShellCommand(RepositoryRoot(), command);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var terminal = TerminalSummary(result);
        var diagnostic = Assert.Single(terminal.GetProperty("diagnostics").EnumerateArray()).GetString();
        Assert.Contains("CLEANUP FAILURE", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MtpPublicWrappersPrintTerminalSummaryForUnknownPartition()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var partitionResult = sandbox.RunPartition("Typo");
        var summaryResult = sandbox.RunSummary(partition: "Typo");

        Assert.Equal(21, partitionResult.ExitCode);
        Assert.Equal(21, summaryResult.ExitCode);
        Assert.Equal("failed", TerminalSummary(partitionResult).GetProperty("outcome").GetString());
        Assert.Equal("failed", TerminalSummary(summaryResult).GetProperty("outcome").GetString());
    }

    [Xunit.Fact]
    public void MtpSummaryPrintsTerminalSummaryForFilterPartitionConflict()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var result = sandbox.RunSummary(partition: "GoalWorktree", filter: "DisplayName~conflict");

        Assert.Equal(21, result.ExitCode);
        Assert.Contains("FILTER FAILURE", result.Stdout, StringComparison.Ordinal);
        Assert.Equal("failed", TerminalSummary(result).GetProperty("outcome").GetString());
    }

    [Xunit.Fact]
    public void MtpPublicWrappersPrintTerminalSummaryForManifestFailure()
    {
        using var sandbox = ScriptSandbox.Create("success");
        sandbox.DeleteManifest();

        var partitionResult = sandbox.RunPartition("GoalWorktree");
        var summaryResult = sandbox.RunSummary(partition: "GoalWorktree");

        Assert.Equal(20, partitionResult.ExitCode);
        Assert.Equal(20, summaryResult.ExitCode);
        Assert.Equal("failed", TerminalSummary(partitionResult).GetProperty("outcome").GetString());
        Assert.Equal("failed", TerminalSummary(summaryResult).GetProperty("outcome").GetString());
    }

    [Xunit.Fact]
    public void MtpCmdRunnerPreservesMetacharactersAndPercentExpansionsInPathsAsArgumentData()
    {
        using var sandbox = ScriptSandbox.Create("success", rootNamePrefix: "meta%SystemRoot%&chars");

        var metacharacterResultsRoot = Path.Combine(
            sandbox.ResultsRoot,
            "meta%SystemRoot%&chars-output");
        var result = sandbox.RunPartition("GoalWorktree", resultsRoot: metacharacterResultsRoot);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.True(File.Exists(sandbox.ArgumentLog), result.Stdout + result.Stderr);
        Assert.Contains(File.ReadAllLines(sandbox.ArgumentLog), argument =>
            argument.Contains("meta%SystemRoot%&chars", StringComparison.Ordinal));
        Assert.Equal("completed", TerminalSummary(result).GetProperty("outcome").GetString());
    }

    [Xunit.Fact]
    public void MtpExitConfirmationWaitsForRedirectedOutputDrain()
    {
        using var sandbox = ScriptSandbox.Create("late-output");

        var result = sandbox.RunPartition("GoalWorktree");

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("late descendant output", result.Stdout, StringComparison.Ordinal);
        Assert.True(TerminalSummary(result).GetProperty("exitConfirmed").GetBoolean());
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

    [Xunit.Fact(DisplayName = "MTP_partition_build_stderr_does_not_override_a_successful_exit_code")]
    public void MtpPartitionBuildStderrDoesNotOverrideSuccessfulExitCode()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var buildMarker = Path.Combine(sandbox.Root, "build-started.txt");
        var fakeDotnet = sandbox.CreateBuildStub(exitCode: 0, buildMarker, diagnosticToStderr: true);

        var result = sandbox.RunPartition("GoalWorktree", noBuild: false, dotnetPath: fakeDotnet);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.Contains("compiler diagnostic from stub", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("Build succeeded.", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("stub stdout", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("BUILD FAILURE", result.Stdout, StringComparison.Ordinal);
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
        Xunit.Assert.Contains("[System.Diagnostics.Process]::new()", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("[switch]$AllowBreakaway", sources[1], StringComparison.Ordinal);
        Xunit.Assert.Contains("-AllowBreakaway:$AllowBreakaway", sources[1], StringComparison.Ordinal);
        Xunit.Assert.Contains("JobObjectLimitBreakawayOk", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("[McgMtpOwnedJob]::new($AllowBreakaway.IsPresent)", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("/PID $($Process.Id) /T /F", sources[2], StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("/IM ", sources[2], StringComparison.OrdinalIgnoreCase);
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

    private static ProcessResult Run(ProcessStartInfo startInfo, TimeSpan? timeout = null)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timeoutMilliseconds = checked((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds);
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            throw new TimeoutException($"{startInfo.FileName} did not exit within {timeoutMilliseconds / 1000} seconds.");
        }
        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static ProcessResult RunAfterStdoutGate(
        ProcessStartInfo startInfo,
        string stdoutGate,
        string releasePath)
    {
        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputGate = new object();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }
            lock (outputGate)
            {
                stdout.AppendLine(args.Data);
            }
            if (args.Data.Contains(stdoutGate, StringComparison.Ordinal) && !File.Exists(releasePath))
            {
                File.WriteAllText(releasePath, "release");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                lock (outputGate)
                {
                    stderr.AppendLine(args.Data);
                }
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            throw new TimeoutException($"{startInfo.FileName} did not exit within 30 seconds.");
        }
        process.WaitForExit();
        lock (outputGate)
        {
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }

    private static JsonElement TerminalSummary(ProcessResult result)
    {
        var lines = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var summaries = lines.Where(line => line.StartsWith("MTP_TERMINAL_SUMMARY ", StringComparison.Ordinal)).ToArray();
        Assert.Single(summaries);
        Assert.Equal(summaries[0], lines[^1]);
        using var document = JsonDocument.Parse(summaries[0]["MTP_TERMINAL_SUMMARY ".Length..]);
        return document.RootElement.Clone();
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class ScriptSandbox : IDisposable
    {
        private ScriptSandbox(
            string root,
            string localApplicationDataRoot,
            string resultsRoot,
            string runnerPath,
            string argumentLog,
            string lockPath,
            string readyPath,
            string releasePath)
        {
            Root = root;
            LocalApplicationDataRoot = localApplicationDataRoot;
            ResultsRoot = resultsRoot;
            RunnerPath = runnerPath;
            ArgumentLog = argumentLog;
            LockPath = lockPath;
            ReadyPath = readyPath;
            ReleasePath = releasePath;
        }

        public string Root { get; }
        public string LocalApplicationDataRoot { get; }
        public string ResultsRoot { get; }
        public string RunnerPath { get; }
        public string ArgumentLog { get; }
        public string LockPath { get; }
        public string ReadyPath { get; }
        public string ReleasePath { get; }

        public static ScriptSandbox Create(string behavior, string rootNamePrefix = "sandbox")
        {
            var suffix = Guid.NewGuid().ToString("n")[..8];
            var repositoryRoot = RepositoryRoot();
            var root = Path.Combine(repositoryRoot, $".mtp-{rootNamePrefix}-{suffix}");
            var localApplicationDataRoot = Path.Combine(repositoryRoot, $".mtp-local-{suffix}");
            var resultsRoot = Path.Combine(localApplicationDataRoot, "Temp", "Low", "mcg-tests");
            var scripts = Path.Combine(root, "scripts");
            var config = Path.Combine(root, "config");
            var projectDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(resultsRoot);

            foreach (var fileName in new[] { "Invoke-InfrastructureTestPartition.ps1", "Invoke-TestSummary.ps1", "MtpTestRunner.psm1" })
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
                "late-output" => TrxBody(total: 1, passed: 1, failed: 0),
                "no-trx" => string.Empty,
                "hang" => string.Empty,
                "root-exits-descendant-locks" => string.Empty,
                _ => throw new ArgumentOutOfRangeException(nameof(behavior))
            };
            var exitCode = behavior switch
            {
                "failed" => 3,
                "no-trx" => 7,
                _ => 0
            };
            var escapedTrx = resultBody.Replace("'", "''");
            var lockPath = Path.Combine(root, "hang.lock");
            var readyPath = Path.Combine(root, "hang.ready");
            var releasePath = Path.Combine(root, "root.release");
            var escapedLockPath = lockPath.Replace("'", "''");
            var escapedReadyPath = readyPath.Replace("'", "''");
            var escapedReleasePath = releasePath.Replace("'", "''");
            File.WriteAllText(fakeRunnerScript, $$"""
                param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
                $Arguments | Set-Content -LiteralPath '{{escapedArgumentLog}}'
                Write-Output 'stub stdout'
                Write-Output "stub temp=$env:TEMP tmp=$env:TMP tmpdir=$env:TMPDIR"
                [Console]::Error.WriteLine('stub stderr')
                if ('{{behavior}}' -eq 'hang') {
                    $lock = [System.IO.File]::Open('{{escapedLockPath}}', 'OpenOrCreate', 'ReadWrite', 'None')
                    try {
                        Set-Content -LiteralPath '{{escapedReadyPath}}' -Value 'ready'
                        Write-Output 'hang descendant ready'
                        $never = [System.Threading.ManualResetEvent]::new($false)
                        [void]$never.WaitOne()
                    }
                    finally {
                        $lock.Dispose()
                    }
                }
                if ('{{behavior}}' -eq 'root-exits-descendant-locks') {
                    $childCommand = @'
                $lock = [System.IO.File]::Open('{{escapedLockPath}}', 'OpenOrCreate', 'ReadWrite', 'None')
                try {
                    Set-Content -LiteralPath '{{escapedReadyPath}}' -Value 'ready'
                    $never = [System.Threading.ManualResetEvent]::new($false)
                    [void]$never.WaitOne()
                }
                finally {
                    $lock.Dispose()
                }
                '@
                    $childEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
                    Start-Process powershell.exe -ArgumentList @('-NoProfile', '-EncodedCommand', $childEncoded) -NoNewWindow | Out-Null
                    while (-not (Test-Path -LiteralPath '{{escapedReadyPath}}')) {
                        [System.Threading.Thread]::Sleep(25)
                    }
                    Write-Output 'hang descendant ready'
                    while (-not (Test-Path -LiteralPath '{{escapedReleasePath}}')) {
                        [System.Threading.Thread]::Sleep(25)
                    }
                    Write-Output 'root released after wrapper timeout'
                    exit 0
                }
                if ('{{behavior}}' -eq 'late-output') {
                    $lateCommand = "Start-Sleep -Seconds 6; Write-Output 'late descendant output'"
                    $lateEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($lateCommand))
                    Start-Process powershell.exe -ArgumentList @('-NoProfile', '-EncodedCommand', $lateEncoded) -NoNewWindow | Out-Null
                }
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
            return new ScriptSandbox(
                root,
                localApplicationDataRoot,
                resultsRoot,
                runnerPath,
                argumentLog,
                lockPath,
                readyPath,
                releasePath);
        }

        public string CreateBuildStub(int exitCode, string markerPath, bool diagnosticToStderr = false)
        {
            var path = Path.Combine(Root, "fake-dotnet.cmd");
            var diagnosticRedirection = diagnosticToStderr ? " 1>&2" : string.Empty;
            File.WriteAllText(path, $"@echo off{Environment.NewLine}echo started>\"{markerPath}\"{Environment.NewLine}echo compiler diagnostic from stub{diagnosticRedirection}{Environment.NewLine}exit /b {exitCode}{Environment.NewLine}");
            return path;
        }

        public ProcessResult RunPartition(
            string partition,
            bool noBuild = true,
            string? dotnetPath = null,
            bool runnerOverride = true,
            string? resultsRoot = null,
            int testHostTimeoutSeconds = 780)
        {
            var startInfo = PartitionStartInfo(
                partition,
                noBuild,
                dotnetPath,
                runnerOverride,
                resultsRoot,
                testHostTimeoutSeconds);
            return Run(startInfo);
        }

        private ProcessStartInfo PartitionStartInfo(
            string partition,
            bool noBuild = true,
            string? dotnetPath = null,
            bool runnerOverride = true,
            string? resultsRoot = null,
            int testHostTimeoutSeconds = 780)
        {
            var startInfo = SandboxPowerShellStartInfo();
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(Root, "scripts", "Invoke-InfrastructureTestPartition.ps1"));
            startInfo.ArgumentList.Add("-Partition");
            startInfo.ArgumentList.Add(partition);
            startInfo.ArgumentList.Add("-ResultsRoot");
            startInfo.ArgumentList.Add(resultsRoot ?? ResultsRoot);
            startInfo.ArgumentList.Add("-TestHostTimeoutSeconds");
            startInfo.ArgumentList.Add(testHostTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            return startInfo;
        }

        public ProcessResult RunPartitionAfterStdoutGate(
            string partition,
            string stdoutGate,
            string releasePath,
            int testHostTimeoutSeconds)
        {
            var startInfo = PartitionStartInfo(partition, testHostTimeoutSeconds: testHostTimeoutSeconds);
            return RunAfterStdoutGate(startInfo, stdoutGate, releasePath);
        }

        public ProcessResult RunSummaryPartition(string partition)
            => RunSummary(partition: partition);

        public ProcessResult RunSummary(string? partition = null, string? filter = null)
        {
            var startInfo = SandboxPowerShellStartInfo();
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(Root, "scripts", "Invoke-TestSummary.ps1"));
            if (partition is not null)
            {
                startInfo.ArgumentList.Add("-Partition");
                startInfo.ArgumentList.Add(partition);
            }
            if (filter is not null)
            {
                startInfo.ArgumentList.Add("-Filter");
                startInfo.ArgumentList.Add(filter);
            }
            startInfo.ArgumentList.Add("-NoBuild");
            startInfo.ArgumentList.Add("-ResultsRoot");
            startInfo.ArgumentList.Add(ResultsRoot);
            startInfo.ArgumentList.Add("-RunnerPath");
            startInfo.ArgumentList.Add(RunnerPath);
            return Run(startInfo);
        }

        public void DeleteManifest() => File.Delete(Path.Combine(Root, "config", "acceptance-manifest.json"));

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
            var startInfo = SandboxPowerShellStartInfo();
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            return Run(startInfo);
        }

        private ProcessStartInfo SandboxPowerShellStartInfo()
        {
            var startInfo = PowerShellStartInfo(Root);
            startInfo.Environment["LOCALAPPDATA"] = LocalApplicationDataRoot;
            return startInfo;
        }

        public void Dispose()
        {
            TryDelete(Root);
            TryDelete(LocalApplicationDataRoot);
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
