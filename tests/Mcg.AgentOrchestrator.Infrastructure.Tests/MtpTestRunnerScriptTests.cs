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
    // The same unchanged case passed in 00:06:02.49 on 2026-08-21; this budget is a hang guard, not a performance assertion.
    internal static readonly TimeSpan NativeMtpRealProcessHangGuard = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ProcessControlReadinessHangGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RealProcessControlShortHangGuard = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RealProcessControlCompletionDelay = TimeSpan.FromSeconds(5);

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

    [Xunit.Theory(DisplayName = "Managed_MTP_runner_executes_every_repository_test_project_after_isolated_build")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "RepositoryChangeClassifierTests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj", "ProviderDefaultTests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj", "CliArgumentNormalizationTests")]
    public void ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild(string project, string testClass)
    {
        MtpTestRunnerScriptTestSupport.RunManagedProjectContract(project, testClass);
    }

    [Xunit.Fact]
    public void ManagedProjectTheoriesRetainAllRepositoryProjectCases()
    {
        (string Project, string TestClass)[] expected =
        [
            ("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "RepositoryChangeClassifierTests"),
            ("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "ProcessStartInfoSourceGuardTests"),
            ("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj", "ProviderDefaultTests"),
            ("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj", "CliArgumentNormalizationTests"),
            ("tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj", "DashboardValidationHarnessTests")
        ];
        var theoryMethods = new[]
        {
            typeof(MtpTestRunnerScriptTests).GetMethod(nameof(ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild))!,
            typeof(MtpTestRunnerScriptTestsManagedProjectRebuild).GetMethod(nameof(MtpTestRunnerScriptTestsManagedProjectRebuild.ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild))!
        };
        var actual = theoryMethods
            .SelectMany(method => method.CustomAttributes)
            .Where(attribute => attribute.AttributeType == typeof(Xunit.InlineDataAttribute))
            .Select(InlineDataPair)
            .OrderBy(item => item.Project, StringComparer.Ordinal)
            .ToArray();

        Xunit.Assert.Equal(expected.OrderBy(item => item.Project, StringComparer.Ordinal), actual);
    }

    private static (string Project, string TestClass) InlineDataPair(System.Reflection.CustomAttributeData attribute)
    {
        var arguments = (IReadOnlyList<System.Reflection.CustomAttributeTypedArgument>)attribute
            .ConstructorArguments
            .Single()
            .Value!;
        return ((string)arguments[0].Value!, (string)arguments[1].Value!);
    }

    [Xunit.Fact(DisplayName = "Real_process_guard_fires_when_child_outlives_injected_budget_and_passes_with_production_guard")]
    public void RealProcessGuardFiresWhenChildOutlivesInjectedBudgetAndPassesWithProductionGuard()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-mtp-real-process-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var shortReady = Path.Combine(root, "short.ready");
            var shortStartInfo = RealProcessControlStartInfo(
                root,
                shortReady,
                neverExits: false,
                completionDelay: RealProcessControlCompletionDelay);

            Xunit.Assert.True(
                RealProcessControlCompletionDelay > RealProcessControlShortHangGuard,
                "The real child delay must exceed the injected guard by construction.");

            var timeout = Xunit.Assert.Throws<RealProcessHangGuardException>(
                () => Run(shortStartInfo, RealProcessControlShortHangGuard, readinessPath: shortReady));

            Xunit.Assert.Contains(
                $"{RealProcessControlShortHangGuard.TotalSeconds:0} seconds",
                timeout.Message,
                StringComparison.Ordinal);
            Xunit.Assert.Contains("elapsed=", timeout.Message, StringComparison.Ordinal);
            Xunit.Assert.True(timeout.RootExited, "The short-guard process must be reaped.");

            var generousReady = Path.Combine(root, "generous.ready");
            var generousResult = Run(
                RealProcessControlStartInfo(
                    root,
                    generousReady,
                    neverExits: false,
                    completionDelay: RealProcessControlCompletionDelay),
                NativeMtpRealProcessHangGuard,
                readinessPath: generousReady);

            Xunit.Assert.Equal(0, generousResult.ExitCode);
            Xunit.Assert.True(File.Exists(generousReady), "The generous arm must execute the real child command.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "Real_process_guard_fails_and_reaps_when_dependency_never_exits")]
    public void RealProcessGuardFailsAndReapsWhenDependencyNeverExits()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-mtp-real-process-never-exits", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var readyPath = Path.Combine(root, "never.ready");
            var descendantPidPath = Path.Combine(root, "descendant.pid");
            var guard = TimeSpan.FromSeconds(2);
            var startInfo = RealProcessControlStartInfo(
                root,
                readyPath,
                neverExits: true,
                descendantPidPath: descendantPidPath);

            var timeout = Xunit.Assert.Throws<RealProcessHangGuardException>(
                () => Run(startInfo, guard, readinessPath: readyPath, descendantPidPath: descendantPidPath));

            Xunit.Assert.Contains($"{guard.TotalSeconds:0} seconds", timeout.Message, StringComparison.Ordinal);
            Xunit.Assert.Contains("elapsed=", timeout.Message, StringComparison.Ordinal);
            Xunit.Assert.True(timeout.RootExited, "The guarded PowerShell root must be reaped.");
            Xunit.Assert.True(timeout.DescendantExited, "The never-exiting descendant must be reaped.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

    [Xunit.Fact]
    public void InitializeResultsDirectory_WritesAtomicOwnershipSidecar()
    {
        var module = Path.Combine(RepositoryRoot(), "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        var nonce = Guid.NewGuid().ToString("N");
        var command = $"Import-Module '{module}' -Force; " +
            $"$root = Join-Path (Get-DefaultMtpResultsRoot) 'sidecar-{nonce}'; " +
            "try { $env:MCG_ACCEPTANCE_GATE_ATTEMPT_ID = 'attempt-123'; " +
            "$dir = Initialize-MtpResultsDirectory -ResultsRoot $root -RunLabel 'ownership'; " +
            "Get-Content -Raw (Join-Path $dir '.mtp-run-ownership.json') } " +
            "finally { Remove-Item Env:MCG_ACCEPTANCE_GATE_ATTEMPT_ID -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }";

        var result = RunPowerShellCommand(RepositoryRoot(), command);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout.Trim());
        Xunit.Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Xunit.Assert.Equal("attempt-123", document.RootElement.GetProperty("attemptId").GetString());
        Xunit.Assert.Equal("ownership", document.RootElement.GetProperty("runLabel").GetString());
        Xunit.Assert.True(document.RootElement.GetProperty("ownerProcessId").GetInt32() > 0);
    }

    [Xunit.Fact(DisplayName = "MTP_results_directory_reports_removal_of_the_unowned_run_directory_when_the_sidecar_write_fails")]
    public void InitializeResultsDirectory_RemovesAndReportsRunDirectoryWhenOwnershipWriteFails()
    {
        var module = Path.Combine(RepositoryRoot(), "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        var nonce = Guid.NewGuid().ToString("N");
        var command = $"$module = Import-Module '{module}' -Force -PassThru; " +
            "& $module { Set-Item -Path 'function:script:Write-MtpRunOwnershipSidecar' -Value { param([string]$ResultsDirectory, [string]$RunLabel, [string]$AttemptId) return $false } }; " +
            $"$root = Join-Path (Get-DefaultMtpResultsRoot) 'sidecar-reject-{nonce}'; " +
            "try { $message = ''; " +
            "try { [void](Initialize-MtpResultsDirectory -ResultsRoot $root -RunLabel 'ownership') } catch { $message = $_.Exception.Message } " +
            "$leftovers = @(if (Test-Path -LiteralPath $root) { Get-ChildItem -LiteralPath $root -Force | ForEach-Object { $_.Name } }); " +
            "[ordered]@{ message = $message; leftovers = $leftovers } | ConvertTo-Json -Compress } " +
            "finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }";

        var result = RunPowerShellCommand(RepositoryRoot(), command);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout.Trim());
        var message = document.RootElement.GetProperty("message").GetString();
        Xunit.Assert.Contains("undecidable to retention sweeps, so no run was started", message, StringComparison.Ordinal);
        // The removal outcome is stated, never assumed: a swallowed failure here would claim the
        // orphan was cleaned up while leaving a directory every retention sweep must call undecidable.
        Xunit.Assert.Contains("No unowned run directory was left behind.", message, StringComparison.Ordinal);
        Xunit.Assert.Empty(document.RootElement.GetProperty("leftovers").EnumerateArray());
    }

    [Xunit.Fact(DisplayName = "MTP_results_directory_names_the_surviving_unowned_orphan_when_its_removal_also_fails")]
    public void InitializeResultsDirectory_NamesOrphanWhenRemovalAlsoFails()
    {
        var module = Path.Combine(RepositoryRoot(), "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        var nonce = Guid.NewGuid().ToString("N");
        // The sidecar writer leaves an exclusively opened file behind, so the orphan removal is forced
        // to fail. Silence here would report a clean failure while a sweep-undecidable directory lives on.
        var command = $"$module = Import-Module '{module}' -Force -PassThru; " +
            "$global:McgLockStream = $null; " +
            "& $module { Set-Item -Path 'function:script:Write-MtpRunOwnershipSidecar' -Value { " +
            "param([string]$ResultsDirectory, [string]$RunLabel, [string]$AttemptId) " +
            "$global:McgLockStream = [System.IO.File]::Open((Join-Path $ResultsDirectory 'locked.bin'), 'CreateNew', 'Write', 'None'); return $false } }; " +
            $"$root = Join-Path (Get-DefaultMtpResultsRoot) 'sidecar-locked-{nonce}'; " +
            "try { $message = ''; " +
            "try { [void](Initialize-MtpResultsDirectory -ResultsRoot $root -RunLabel 'ownership') } catch { $message = $_.Exception.Message } " +
            "[ordered]@{ message = $message } | ConvertTo-Json -Compress } " +
            "finally { if ($null -ne $global:McgLockStream) { $global:McgLockStream.Dispose() } " +
            "Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }";

        var result = RunPowerShellCommand(RepositoryRoot(), command);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout.Trim());
        var message = document.RootElement.GetProperty("message").GetString();
        Xunit.Assert.Contains("could also not be removed", message, StringComparison.Ordinal);
        Xunit.Assert.Contains("until it is deleted by hand", message, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("No unowned run directory was left behind", message, StringComparison.Ordinal);
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
        // A failing summary must still separate what coverage was requested from what actually ran;
        // otherwise a zero-test or failed partition reports neither, and the distinction only exists
        // on green runs where it is least needed.
        var requestedFilters = terminal.GetProperty("requestedFilters").EnumerateArray()
            .Select(filter => filter.GetString())
            .ToArray();
        Assert.Equal(["FullyQualifiedName~GoalWorktreeTests"], requestedFilters);
        var executedTestNames = terminal.GetProperty("executedTestNames").EnumerateArray()
            .Select(name => name.GetString())
            .ToArray();
        if (behavior == "failed")
        {
            Assert.Equal(["stub failure"], executedTestNames);
        }
        else
        {
            Assert.Empty(executedTestNames);
        }
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
            "%CD%&");
        var result = sandbox.RunPartition("GoalWorktree", resultsRoot: metacharacterResultsRoot);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.True(File.Exists(sandbox.ArgumentLog), result.Stdout + result.Stderr);
        Assert.Contains(File.ReadAllLines(sandbox.ArgumentLog), argument =>
            argument.Contains("%CD%&", StringComparison.Ordinal));
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

    [Xunit.Fact(DisplayName = "MTP_partition_build_failure_is_loud_and_never_launches_stale_runner")]
    public void MtpPartitionBuildFailureIsLoudAndNeverLaunchesStaleRunner()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var buildMarker = Path.Combine(sandbox.Root, "build-started.txt");
        var fakeDotnet = sandbox.CreateBuildStub(exitCode: 9, buildMarker);

        var result = sandbox.RunPartition("GoalWorktree", noBuild: false, dotnetPath: fakeDotnet);

        Xunit.Assert.Equal(23, result.ExitCode);
        Xunit.Assert.Contains("compiler diagnostic from stub", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("BUILD FAILURE", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("managed MTP runner was not launched", result.Stdout, StringComparison.Ordinal);
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

    [Xunit.Fact]
    public void MtpBuildLaunchUsesNoWindowProcessStartThroughCmdShim()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var buildMarker = Path.Combine(sandbox.Root, "build-started.txt");
        var launchLog = Path.Combine(sandbox.Root, "build-launch.txt");
        var fakeDotnet = sandbox.CreateBuildStub(
            exitCode: 0,
            buildMarker,
            diagnosticToStderr: true,
            launchLogPath: launchLog);

        var result = sandbox.RunPartition("GoalWorktree", noBuild: false, dotnetPath: fakeDotnet);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.True(File.Exists(buildMarker), result.Stdout + result.Stderr);
        Xunit.Assert.True(File.Exists(launchLog), result.Stdout + result.Stderr);
        var launchLines = File.ReadAllLines(launchLog);
        Xunit.Assert.Contains(launchLines, line =>
            line.StartsWith("CMDCMDLINE=", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("/d /s /c", StringComparison.OrdinalIgnoreCase));
        var arguments = launchLines
            .Where(line => line.StartsWith("ARG=", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["ARG=".Length..])
            .ToArray();
        var project = Path.Combine(
            sandbox.Root,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        Xunit.Assert.Equal("build", arguments[0]);
        Xunit.Assert.Equal(project, arguments[1], ignoreCase: true);
        Xunit.Assert.Equal(["--configuration", "Debug", "--output"], arguments[2..5]);
        Xunit.Assert.StartsWith(sandbox.ResultsRoot, arguments[5], StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(
            Path.Combine(".c", ".b-"),
            arguments[5],
            StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Equal(["--nologo", "--verbosity", "minimal", "-clp:ErrorsOnly;Summary", "-fl"], arguments[6..11]);
        Xunit.Assert.StartsWith("-flp:LogFile=", arguments[11], StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.EndsWith(";Verbosity=Normal", arguments[11], StringComparison.Ordinal);
        Xunit.Assert.Equal("-nodeReuse:false", arguments[12]);
        Xunit.Assert.True(
            arguments.Any(argument => argument.Equals(
                $"-p:McgBuildReceiptProject={project}",
                StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, arguments));
        Xunit.Assert.Contains("compiler diagnostic from stub", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("Build succeeded.", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MtpBuildLaunchPreservesPercentAndMetacharactersInPaths()
    {
        using var sandbox = ScriptSandbox.Create("success", rootNamePrefix: "build-meta%SystemRoot%&chars");
        var buildMarker = Path.Combine(sandbox.Root, "build-started.txt");
        var launchLog = Path.Combine(sandbox.Root, "meta-build-launch.txt");
        var fakeDotnet = sandbox.CreateBuildStub(exitCode: 0, buildMarker, launchLogPath: launchLog);

        var result = sandbox.RunPartition("GoalWorktree", noBuild: false, dotnetPath: fakeDotnet);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.True(File.Exists(buildMarker), result.Stdout + result.Stderr);
        Xunit.Assert.Contains("Build succeeded.", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("build-meta%SystemRoot%&chars", result.Stdout, StringComparison.Ordinal);
        var arguments = File.ReadAllLines(launchLog)
            .Where(line => line.StartsWith("ARG=", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["ARG=".Length..])
            .ToArray();
        Xunit.Assert.Contains(arguments, argument => argument.Equals(
            Path.Combine(
                sandbox.Root,
                "tests",
                "Mcg.AgentOrchestrator.Infrastructure.Tests",
                "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.Contains(arguments, argument =>
            argument.StartsWith("-flp:LogFile=", StringComparison.OrdinalIgnoreCase) &&
            argument.EndsWith(";Verbosity=Normal", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void MtpBuildStartInfoSuppressesWindowsForExeAndCmdTargets()
    {
        var module = Path.Combine(RepositoryRoot(), "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        var command =
            $"Import-Module '{module}' -Force; " +
            "$module = Get-Module MtpTestRunner; " +
            "$result = & $module { " +
            "$exe = New-MtpProcessStartInfo -Executable 'C:\\tools\\dotnet.exe' -Arguments @('C:\\tools\\dotnet.exe','build','-nodeReuse:false'); " +
            "$cmd = New-MtpProcessStartInfo -Executable 'C:\\tools\\dotnet.cmd' -Arguments @('C:\\tools\\dotnet.cmd','build','-nodeReuse:false'); " +
            "[ordered]@{ exeCreateNoWindow = $exe.CreateNoWindow; exeUseShellExecute = $exe.UseShellExecute; exeRedirectOut = $exe.RedirectStandardOutput; exeRedirectError = $exe.RedirectStandardError; exeFileName = $exe.FileName; cmdCreateNoWindow = $cmd.CreateNoWindow; cmdUseShellExecute = $cmd.UseShellExecute; cmdRedirectOut = $cmd.RedirectStandardOutput; cmdRedirectError = $cmd.RedirectStandardError; cmdArguments = $cmd.Arguments } }; " +
            "$result | ConvertTo-Json -Compress";

        var result = RunPowerShellCommand(RepositoryRoot(), command);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout.Trim());
        var root = document.RootElement;
        Xunit.Assert.True(root.GetProperty("exeCreateNoWindow").GetBoolean());
        Xunit.Assert.False(root.GetProperty("exeUseShellExecute").GetBoolean());
        Xunit.Assert.True(root.GetProperty("exeRedirectOut").GetBoolean());
        Xunit.Assert.True(root.GetProperty("exeRedirectError").GetBoolean());
        Xunit.Assert.Equal("C:\\tools\\dotnet.exe", root.GetProperty("exeFileName").GetString(), ignoreCase: true);
        Xunit.Assert.True(root.GetProperty("cmdCreateNoWindow").GetBoolean());
        Xunit.Assert.False(root.GetProperty("cmdUseShellExecute").GetBoolean());
        Xunit.Assert.True(root.GetProperty("cmdRedirectOut").GetBoolean());
        Xunit.Assert.True(root.GetProperty("cmdRedirectError").GetBoolean());
        Xunit.Assert.StartsWith("/d /s /c", root.GetProperty("cmdArguments").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void MtpBuildStartFailureIsDistinctFromMonitoringFailure()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var missingDotnet = Path.Combine(sandbox.Root, "missing-dotnet.exe");

        var result = sandbox.RunPartition("GoalWorktree", noBuild: false, dotnetPath: missingDotnet);

        Xunit.Assert.Equal(23, result.ExitCode);
        Xunit.Assert.Contains("BUILD FAILURE - could not start", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("BUILD MONITOR FAILURE", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("BUILD TIMEOUT", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "MTP_closure_no_build_missing_managed_assembly_fails_before_launch")]
    public void MtpNoBuildMissingManagedAssemblyReportsPathAndBuildCommand()
    {
        using var sandbox = ScriptSandbox.Create("success");

        var result = sandbox.RunPartition(
            "GoalWorktree",
            dotnetPath: sandbox.RunnerPath,
            runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("NO-BUILD CLOSURE FAILURE", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("No verified no-build output exists", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("Repair: dotnet build", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("was not copied or executed", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain("NO TRX", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "MTP_closure_managed_runner_does_not_require_native_apphost")]
    public void MtpManagedRunnerDoesNotRequireNativeApphost()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var managedAssembly = sandbox.CreateManagedAssemblyPlaceholder();

        var result = sandbox.RunPartition(
            "GoalWorktree",
            dotnetPath: sandbox.RunnerPath,
            runnerOverride: false);

        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Xunit.Assert.True(File.Exists(managedAssembly));
        Xunit.Assert.False(File.Exists(Path.ChangeExtension(managedAssembly, ".exe")));
        var launchedAssembly = File.ReadAllLines(sandbox.ArgumentLog)[0];
        Xunit.Assert.False(
            string.Equals(managedAssembly, launchedAssembly, StringComparison.OrdinalIgnoreCase),
            $"Managed runner launched mutable shared output instead of a sealed closure: {launchedAssembly}");
        Xunit.Assert.StartsWith(sandbox.ResultsRoot, launchedAssembly, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(
            Path.Combine(".c"),
            launchedAssembly,
            StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.EndsWith(
            Path.Combine("Mcg.AgentOrchestrator.Infrastructure.Tests.dll"),
            launchedAssembly,
            StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain("MISSING APPHOST", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_requires_a_matching_build_receipt_before_launch")]
    public void MtpNoBuildRequiresMatchingBuildReceiptBeforeLaunch()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var managedAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        File.Delete(Path.Combine(Path.GetDirectoryName(managedAssembly)!, ".mcg-build-receipt.txt"));

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("build receipt verification failed", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("McgIsolatedArtifactsPath", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog));
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_source_changed_after_receipted_build")]
    public void MtpNoBuildRejectsSourceChangedAfterReceiptedBuild()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var managedAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(managedAssembly)!, "receipt-source.cs"), "// changed after build");

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("receipt source checksum mismatch", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog));
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_two_receipt_verified_candidates_with_different_assemblies")]
    public void MtpNoBuildRejectsAmbiguousVerifiedCandidatesBeforeLaunch()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var declaredAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        var declaredDirectory = Path.GetDirectoryName(declaredAssembly)!;
        var evaluatedDirectory = Path.Combine(
            sandbox.Root,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "bin",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(evaluatedDirectory);
        foreach (var source in Directory.EnumerateFiles(declaredDirectory))
        {
            File.Copy(source, Path.Combine(evaluatedDirectory, Path.GetFileName(source)), overwrite: true);
        }

        var evaluatedAssembly = Path.Combine(evaluatedDirectory, Path.GetFileName(declaredAssembly));
        using (var stream = new FileStream(evaluatedAssembly, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(0);
        }
        var receiptPath = Path.Combine(evaluatedDirectory, ".mcg-build-receipt.txt");
        var receipt = File.ReadAllText(receiptPath)
            .Replace(declaredDirectory + Path.DirectorySeparatorChar, evaluatedDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .Replace(
                $"S\tsource\t{Path.Combine(evaluatedDirectory, "receipt-source.cs")}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(declaredDirectory, "receipt-source.cs"))))}",
                $"S\tsource\t{Path.Combine(declaredDirectory, "receipt-source.cs")}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(declaredDirectory, "receipt-source.cs"))))}",
                StringComparison.Ordinal)
            .Replace(
                $"K\tassemblySha256\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(declaredAssembly)))}",
                $"K\tassemblySha256\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(evaluatedAssembly)))}",
                StringComparison.Ordinal)
            .Replace(
                $"F\tclosure\t{evaluatedAssembly}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(declaredAssembly)))}",
                $"F\tclosure\t{evaluatedAssembly}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(evaluatedAssembly)))}",
                StringComparison.Ordinal);
        File.WriteAllText(receiptPath, receipt);

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("ambiguous verified no-build outputs", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(declaredDirectory, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(evaluatedDirectory, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "The runner must not launch an ambiguous build output.");
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_two_receipt_verified_candidates_with_different_closure_dependencies")]
    public void MtpNoBuildRejectsAmbiguousVerifiedCandidatesWithDifferentCopiedDependenciesBeforeLaunch()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var declaredAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        var declaredDirectory = Path.GetDirectoryName(declaredAssembly)!;
        var evaluatedDirectory = Path.Combine(
            sandbox.Root,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "bin",
            "Debug",
            "net10.0");
        Directory.CreateDirectory(evaluatedDirectory);
        foreach (var source in Directory.EnumerateFiles(declaredDirectory))
        {
            File.Copy(source, Path.Combine(evaluatedDirectory, Path.GetFileName(source)), overwrite: true);
        }

        var evaluatedDependency = Path.Combine(evaluatedDirectory, "Mcg.AgentOrchestrator.App.dll");
        using (var stream = new FileStream(evaluatedDependency, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.WriteByte(0);
        }

        var receiptPath = Path.Combine(evaluatedDirectory, ".mcg-build-receipt.txt");
        var receipt = File.ReadAllText(receiptPath)
            .Replace(declaredDirectory + Path.DirectorySeparatorChar, evaluatedDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .Replace(
                $"S\tsource\t{Path.Combine(evaluatedDirectory, "receipt-source.cs")}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(declaredDirectory, "receipt-source.cs"))))}",
                $"S\tsource\t{Path.Combine(declaredDirectory, "receipt-source.cs")}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(declaredDirectory, "receipt-source.cs"))))}",
                StringComparison.Ordinal)
            .Replace(
                $"F\tclosure\t{evaluatedDependency}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(declaredDirectory, "Mcg.AgentOrchestrator.App.dll"))))}",
                $"F\tclosure\t{evaluatedDependency}\t{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(evaluatedDependency)))}",
                StringComparison.Ordinal);
        File.WriteAllText(receiptPath, receipt);

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("ambiguous verified no-build outputs", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(declaredDirectory, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains(evaluatedDirectory, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("closureSha256=", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "The runner must not launch a closure whose copied dependencies identify a different build.");
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_receipt_for_a_different_target_framework_before_launch")]
    public void MtpNoBuildRejectsReceiptForDifferentTargetFrameworkBeforeLaunch()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var managedAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        var receiptPath = Path.Combine(Path.GetDirectoryName(managedAssembly)!, ".mcg-build-receipt.txt");
        File.WriteAllText(
            receiptPath,
            File.ReadAllText(receiptPath).Replace("K\ttargetFramework\tnet10.0", "K\ttargetFramework\tnet9.0", StringComparison.Ordinal));

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("receipt key 'targetFramework' does not match: recorded='net9.0' expected='net10.0'", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "The runner must not launch a receipt for a different target framework.");
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_receipt_for_a_different_runtime_identifier_before_launch")]
    public void MtpNoBuildRejectsReceiptForDifferentRuntimeIdentifierBeforeLaunch()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var managedAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        var receiptPath = Path.Combine(Path.GetDirectoryName(managedAssembly)!, ".mcg-build-receipt.txt");
        File.WriteAllText(
            receiptPath,
            File.ReadAllText(receiptPath).Replace("K\truntimeIdentifier\t", "K\truntimeIdentifier\twin-x64", StringComparison.Ordinal));

        var result = sandbox.RunPartition("GoalWorktree", dotnetPath: sandbox.RunnerPath, runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("receipt key 'runtimeIdentifier' does not match: recorded='win-x64' expected=''", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "The runner must not launch a receipt for a different runtime identifier.");
    }

    [Xunit.Fact(DisplayName = "MTP_closure_no_build_never_launches_mixed_or_unversioned_repository_closure")]
    public void MtpNoBuildNeverLaunchesMixedOrUnversionedRepositoryClosure()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var managedAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        var outputDirectory = Path.GetDirectoryName(managedAssembly)!;
        var corruptedDependency = Path.Combine(outputDirectory, "Mcg.AgentOrchestrator.App.dll");
        Xunit.Assert.True(File.Exists(corruptedDependency));
        File.WriteAllText(corruptedDependency, "stale app from a different build");

        var result = sandbox.RunPartition(
            "GoalWorktree",
            dotnetPath: sandbox.RunnerPath,
            runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.Contains("NO-BUILD CLOSURE FAILURE", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.Contains("receipt closure file checksum mismatch", result.Stdout, StringComparison.Ordinal);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "The fake dotnet runner must not be launched for an unsealed closure.");
    }

    [Xunit.Fact(DisplayName = "MTP_no_build_rejects_a_receipt_verified_output_with_a_changed_copied_dependency_before_launch")]
    public void MtpNoBuildRejectsVerifiedOutputWithChangedCopiedDependencyBeforeLaunch()
    {
        using var sandbox = ScriptSandbox.Create("success");
        var managedAssembly = sandbox.CreateManagedAssemblyPlaceholder();
        var outputDirectory = Path.GetDirectoryName(managedAssembly)!;
        var copiedDependency = Path.Combine(outputDirectory, "Mcg.AgentOrchestrator.App.dll");
        var differentValidDependency = Path.Combine(outputDirectory, "Mcg.AgentOrchestrator.Core.dll");
        Xunit.Assert.True(File.Exists(copiedDependency));
        Xunit.Assert.True(File.Exists(differentValidDependency));
        File.Copy(differentValidDependency, copiedDependency, overwrite: true);

        var result = sandbox.RunPartition(
            "GoalWorktree",
            dotnetPath: sandbox.RunnerPath,
            runnerOverride: false);

        Xunit.Assert.Equal(24, result.ExitCode);
        Xunit.Assert.False(File.Exists(sandbox.ArgumentLog), "The fake dotnet runner must not launch a closure with a changed copied dependency.");
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
        var scriptsDirectory = Path.Combine(root, "scripts");
        var checkedInScripts = Directory.EnumerateFiles(scriptsDirectory, "*.ps1", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(scriptsDirectory, "*.psm1", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Xunit.Assert.NotEmpty(checkedInScripts);
        foreach (var scriptPath in checkedInScripts)
        {
            Xunit.Assert.DoesNotContain(
                "dotnet test",
                File.ReadAllText(scriptPath),
                StringComparison.OrdinalIgnoreCase);
        }

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
        Xunit.Assert.DoesNotContain("& $DotnetPath build", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "$startInfo = New-MtpProcessStartInfo -Executable $Executable -Arguments $Arguments -StartupHookPath $StartupHookPath",
            sources[2],
            StringComparison.Ordinal);
        Xunit.Assert.Contains("$startInfo.CreateNoWindow = $true", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("Resolve-MtpManagedAssemblyPath", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("$executable = if ($usesManagedAssembly) { $DotnetPath }", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("'-clp:ErrorsOnly;Summary'", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("Get-MtpBoundedFileName -Stem \"build-$projectName\"", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("-flp:LogFile=$buildLogPath;Verbosity=Normal", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("'-nodeReuse:false'", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("Resolve-MtpFaultDialogStartupHook", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("DOTNET_STARTUP_HOOKS", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("WerFaultReportingNoUi = 0x0020", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("WerGetFlags(GetCurrentProcess(), out werFlags)", sources[2], StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("MtpBuildTimeoutSeconds", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("MonitoringFailureMessage = $monitoringFailureMessage", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("Build output log: $buildLogPath", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Matches(
                sources[2],
                @"\[McgMtpSuppressedProcessStart\]::Start\(",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count);
        Xunit.Assert.DoesNotContain(
            "$executable = if ([string]::IsNullOrWhiteSpace($RunnerPath)) { $expectedAppHost }",
            sources[2],
            StringComparison.Ordinal);
        Xunit.Assert.Contains("[switch]$AllowBreakaway", sources[1], StringComparison.Ordinal);
        Xunit.Assert.Contains("-AllowBreakaway:$AllowBreakaway", sources[1], StringComparison.Ordinal);
        Xunit.Assert.Contains("JobObjectLimitBreakawayOk", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("[McgMtpOwnedJob]::new($AllowBreakaway.IsPresent)", sources[2], StringComparison.Ordinal);
        Xunit.Assert.Contains("/PID $($Process.Id) /T /F", sources[2], StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("/IM ", sources[2], StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("CLEANUP FAILURE", sources[2], StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("$runDirectory -Recurse -Force -ErrorAction SilentlyContinue", sources[2], StringComparison.Ordinal);

        var isolatedRunner = File.ReadAllText(Path.Combine(root, "scripts", "Invoke-IsolatedDotnet.ps1"));
        Xunit.Assert.DoesNotContain("Get-OneStepMtpDotnetTestArguments", isolatedRunner, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("& dotnet @oneStepArguments", isolatedRunner, StringComparison.Ordinal);
        Xunit.Assert.Contains("& dotnet $builtArtifacts.AssemblyPath @mtpArguments", isolatedRunner, StringComparison.Ordinal);

        var dashboardCycle = File.ReadAllText(Path.Combine(root, "scripts", "Invoke-DashboardBuildTestCycle.ps1"));
        Xunit.Assert.DoesNotContain("dotnet test", dashboardCycle, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("Invoke-TestSummary.ps1", dashboardCycle, StringComparison.Ordinal);

        var systemEndpoints = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Dashboard",
            "Api",
            "DashboardEndpoints.System.cs"));
        Xunit.Assert.DoesNotContain(
            "Invoke-IsolatedDotnet.ps1 test Mcg.AgentOrchestrator.sln",
            systemEndpoints,
            StringComparison.Ordinal);
        Xunit.Assert.Contains(
            "Invoke-TestSummary.ps1 -Target .\\\\Mcg.AgentOrchestrator.sln",
            systemEndpoints,
            StringComparison.Ordinal);
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

    private static ProcessStartInfo RealProcessControlStartInfo(
        string workingDirectory,
        string readyPath,
        bool neverExits,
        TimeSpan? completionDelay = null,
        string? descendantPidPath = null)
    {
        static string Quote(string path) => path.Replace("'", "''", StringComparison.Ordinal);

        var startInfo = PowerShellStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("-Command");
        if (neverExits)
        {
            startInfo.ArgumentList.Add(
                "$child = Start-Process powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','while ($true) { Start-Sleep -Seconds 60 }' -WindowStyle Hidden -PassThru; " +
                $"Set-Content -LiteralPath '{Quote(descendantPidPath!)}' -Value $child.Id; " +
                $"Set-Content -LiteralPath '{Quote(readyPath)}' -Value ready; " +
                "while ($true) { Start-Sleep -Seconds 60 }");
        }
        else
        {
            var delay = completionDelay
                ?? throw new ArgumentNullException(nameof(completionDelay), "A completing control must declare its real delay.");
            var delayMilliseconds = checked((int)delay.TotalMilliseconds);
            startInfo.ArgumentList.Add(
                $"Set-Content -LiteralPath '{Quote(readyPath)}' -Value ready; Start-Sleep -Milliseconds {delayMilliseconds}");
        }
        return startInfo;
    }

    internal static ProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan? timeout = null,
        string? readinessPath = null,
        string? descendantPidPath = null)
    {
        Process process;
        using (ProcessTreeGuiSuppression.AcquireErrorModeForChildSpawn())
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        }
        using var processScope = process;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Process? descendant = null;
        if (readinessPath is not null)
        {
            string[] readinessPaths = descendantPidPath is null ? [readinessPath] : [readinessPath, descendantPidPath];
            try
            {
                WaitForFilesAsync(startInfo.WorkingDirectory, readinessPaths, ProcessControlReadinessHangGuard).GetAwaiter().GetResult();
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                process.WaitForExit(10_000);
                stdout.GetAwaiter().GetResult();
                stderr.GetAwaiter().GetResult();
                throw;
            }
            if (descendantPidPath is not null)
            {
                descendant = Process.GetProcessById(int.Parse(File.ReadAllText(descendantPidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var timeoutMilliseconds = checked((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            var rootExited = process.WaitForExit(10_000);
            var descendantExited = descendant is null || descendant.WaitForExit(10_000);
            var timedOutStdout = stdout.GetAwaiter().GetResult();
            var timedOutStderr = stderr.GetAwaiter().GetResult();
            descendant?.Dispose();
            throw new RealProcessHangGuardException(
                $"{startInfo.FileName} did not exit within {timeoutMilliseconds / 1000} seconds; elapsed={stopwatch.Elapsed}." +
                $"{Environment.NewLine}stdout:{Environment.NewLine}{timedOutStdout}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}{timedOutStderr}",
                rootExited,
                descendantExited);
        }
        descendant?.Dispose();
        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static async Task WaitForFilesAsync(string directory, string[] paths, TimeSpan hangGuard)
    {
        if (paths.All(File.Exists))
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };
        FileSystemEventHandler checkReady = (_, _) =>
        {
            if (paths.All(File.Exists))
            {
                ready.TrySetResult();
            }
        };
        watcher.Created += checkReady;
        watcher.Changed += checkReady;
        if (paths.All(File.Exists))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(hangGuard);
        using var registration = timeout.Token.Register(
            () => ready.TrySetException(new Xunit.Sdk.XunitException(
                $"Real-process readiness files did not arrive within {hangGuard.TotalSeconds:0} seconds: {string.Join(", ", paths)}")));
        await ready.Task;
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

    internal static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));

    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class RealProcessHangGuardException(
        string message,
        bool rootExited,
        bool descendantExited) : TimeoutException(message)
    {
        public bool RootExited { get; } = rootExited;
        public bool DescendantExited { get; } = descendantExited;
    }

    internal sealed class ScriptSandbox : IDisposable
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
                # powershell.exe -File binds script parameters, which mangles MSBuild-style argument
                # tokens: '-getResultOutputFile:<path>' arrives as two argv entries and a bare '-p'
                # is swallowed by common-parameter prefix matching. Real dotnet.exe receives each
                # token whole, so the stub collects $args (no param block, nothing is swallowed) and
                # rejoins the known '-switch' + value pairs before matching them.
                $mcgRaw = @($args)
                $mcgArguments = [System.Collections.Generic.List[string]]::new()
                $mcgSplitSwitches = @('-p', '-getProperty', '-getItem', '-getResultOutputFile')
                for ($mcgIndex = 0; $mcgIndex -lt $mcgRaw.Count; $mcgIndex++) {
                    $mcgToken = [string]$mcgRaw[$mcgIndex]
                    if ($mcgSplitSwitches -contains $mcgToken -and ($mcgIndex + 1) -lt $mcgRaw.Count) {
                        $mcgArguments.Add($mcgToken + ':' + [string]$mcgRaw[$mcgIndex + 1])
                        $mcgIndex++
                        continue
                    }
                    $mcgArguments.Add($mcgToken)
                }
                $Arguments = $mcgArguments.ToArray()
                if ($Arguments.Count -gt 0 -and $Arguments[0] -eq 'msbuild') {
                    $targetPath = '{{Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "bin", "Debug", "net10.0", "Mcg.AgentOrchestrator.Infrastructure.Tests.dll")}}'
                    $sourcePath = '{{Path.Combine(root, "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Debug", "receipt-source.cs")}}'
                    $open = [char]123
                    $close = [char]125
                    $payload = ($open + '"Properties":' + $open + '"TargetPath":' + ($targetPath | ConvertTo-Json -Compress) + ',"TargetFramework":"net10.0","RuntimeIdentifier":""' + $close + ',"Items":' + $open + '"Compile":[' + $open + '"Identity":' + ($sourcePath | ConvertTo-Json -Compress) + $close + ']' + $close + $close)
                    # Real MSBuild writes the evaluation payload to -getResultOutputFile, never to the
                    # shared stdout/stderr capture. Fail loudly if the caller stops isolating it.
                    $resultFile = @($Arguments | Where-Object { $_ -like '-getResultOutputFile:*' })
                    if ($resultFile.Count -ne 1) {
                        [Console]::Error.WriteLine('stub msbuild requires exactly one -getResultOutputFile argument')
                        exit 9
                    }
                    Set-Content -LiteralPath $resultFile[0].Substring('-getResultOutputFile:'.Length) -Value $payload -Encoding utf8
                    exit 0
                }
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

        public string CreateBuildStub(
            int exitCode,
            string markerPath,
            bool diagnosticToStderr = false,
            string? launchLogPath = null)
        {
            var path = Path.Combine(Root, "fake-dotnet.cmd");
            var diagnosticRedirection = diagnosticToStderr ? " 1>&2" : string.Empty;
            var lines = new List<string>
            {
                "@echo off",
                "setlocal EnableExtensions EnableDelayedExpansion",
                $"echo started>\"{EscapeBatchPercent(markerPath)}\""
            };
            if (launchLogPath is not null)
            {
                var escapedLaunchLog = EscapeBatchPercent(launchLogPath);
                lines.Add($"echo CMDCMDLINE=%CMDCMDLINE%>\"{escapedLaunchLog}\"");
                lines.Add(":mcg_argument_loop");
                lines.Add("if \"%~1\"==\"\" goto mcg_arguments_done");
                lines.Add("set \"mcg_argument=%~1\"");
                lines.Add($"echo ARG=!mcg_argument!>>\"{escapedLaunchLog}\"");
                lines.Add("shift");
                lines.Add("goto mcg_argument_loop");
                lines.Add(":mcg_arguments_done");
            }
            lines.Add($"echo compiler diagnostic from stub{diagnosticRedirection}");
            lines.Add($"exit /b {exitCode}");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return path;
        }

        private static string EscapeBatchPercent(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

        public string CreateManagedAssemblyPlaceholder()
        {
            var outputDirectory = Path.Combine(
                Root,
                "bin",
                "Mcg.AgentOrchestrator.Infrastructure.Tests",
                "Debug");
            Directory.CreateDirectory(outputDirectory);
            foreach (var source in Directory.EnumerateFiles(
                AppContext.BaseDirectory,
                "Mcg.AgentOrchestrator*.dll",
                SearchOption.TopDirectoryOnly))
            {
                File.Copy(source, Path.Combine(outputDirectory, Path.GetFileName(source)), overwrite: true);
            }
            var depsLeaf = "Mcg.AgentOrchestrator.Infrastructure.Tests.deps.json";
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, depsLeaf),
                Path.Combine(outputDirectory, depsLeaf),
                overwrite: true);
            var path = Path.Combine(outputDirectory, "Mcg.AgentOrchestrator.Infrastructure.Tests.dll");
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            File.Copy(Path.ChangeExtension(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.Infrastructure.Tests.dll"), ".pdb"), pdbPath, overwrite: true);
            var sourcePath = Path.Combine(outputDirectory, "receipt-source.cs");
            File.WriteAllText(sourcePath, "// receipt source");
            var receiptLines = new[]
            {
                "K\tschemaVersion\t2",
                $"K\tproject\t{Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj")}",
                "K\tconfiguration\tDebug",
                "K\ttargetFramework\tnet10.0",
                "K\truntimeIdentifier\t",
                $"K\trepositoryRoot\t{Root}{Path.DirectorySeparatorChar}",
                $"K\toutputDirectory\t{outputDirectory}{Path.DirectorySeparatorChar}",
                "K\tassemblyLeaf\tMcg.AgentOrchestrator.Infrastructure.Tests.dll",
                $"K\tassemblySha256\t{Sha256(path)}",
                $"K\tpdbSha256\t{Sha256(pdbPath)}",
                $"S\tsource\t{sourcePath}\t{Sha256(sourcePath)}"
            }.Concat(Directory.EnumerateFiles(outputDirectory)
                .Where(path => !Path.GetFileName(path).Equals(".mcg-build-receipt.txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => $"F\tclosure\t{path}\t{Sha256(path)}"));
            File.WriteAllLines(Path.Combine(outputDirectory, ".mcg-build-receipt.txt"), receiptLines);
            Xunit.Assert.True(File.Exists(path), $"Expected managed test assembly fixture at '{path}'.");
            return path;
        }

        private static string Sha256(string path)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

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

        public ProcessResult RunPartitionUnderAcceptanceAttempt(string partition, string attemptId)
        {
            var startInfo = PartitionStartInfo(
                partition,
                noBuild: true,
                dotnetPath: RunnerPath,
                runnerOverride: false);
            startInfo.Environment["MCG_ACCEPTANCE_GATE_ATTEMPT_ID"] = attemptId;
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

        public ProcessResult RunPartitionWithRejectedEvidenceOwnership()
        {
            var module = Path.Combine(Root, "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
            var manifest = Path.Combine(Root, "config", "acceptance-manifest.json").Replace("'", "''", StringComparison.Ordinal);
            var root = Root.Replace("'", "''", StringComparison.Ordinal);
            var resultsRoot = ResultsRoot.Replace("'", "''", StringComparison.Ordinal);
            var runner = RunnerPath.Replace("'", "''", StringComparison.Ordinal);
            var command =
                $"Import-Module '{module}' -Force; " +
                $"$manifest = Read-MtpTestManifest '{manifest}'; " +
                "$ownershipWriter = { param($directory, $label) $false }; " +
                $"$run = Invoke-MtpTestRun -RepositoryRoot '{root}' -Manifest $manifest " +
                "-Target 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj' " +
                "-Filters 'FullyQualifiedName~GoalWorktreeTests' -RunLabel 'ownership-failure' -NoBuild " +
                $"-ResultsRoot '{resultsRoot}' -RunnerPath '{runner}' -RetainedEvidenceOwnershipWriter $ownershipWriter; " +
                "$run | ConvertTo-Json -Compress";
            var startInfo = SandboxPowerShellStartInfo();
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
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
            ScriptSandboxCleanup.DeleteOrThrow([Root, LocalApplicationDataRoot]);
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

    }
}

[Xunit.Collection(TestCollections.ProcessSpawning)]
[Xunit.Trait("Category", "AcceptanceOptIn")]
public sealed class MtpTestRunnerScriptTestsManagedProjectRebuild
{
    [Xunit.Theory(DisplayName = "Managed_MTP_runner_executes_large_repository_test_projects_after_isolated_build")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "ProcessStartInfoSourceGuardTests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj", "DashboardValidationHarnessTests")]
    public void ManagedMtpRunnerExecutesEveryRepositoryTestProjectAfterIsolatedBuild(string project, string testClass)
    {
        MtpTestRunnerScriptTestSupport.RunManagedProjectContract(project, testClass);
    }
}

file static class MtpTestRunnerScriptTestSupport
{
    public static void RunManagedProjectContract(string project, string testClass)
    {
        var root = MtpTestRunnerScriptTests.RepositoryRoot();
        var goalId = new GoalId(Guid.NewGuid().ToString("N"));
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "mtp-managed-runner-contract");
        var acquisition = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(
            environment,
            timeout: TimeSpan.FromMinutes(5));
        var acquired = Xunit.Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisition);
        var lease = acquired.Lease;
        var artifactsPath = lease.Environment.ArtifactsPath;
        try
        {
            var buildStartInfo = new ProcessStartInfo
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
            buildStartInfo.Environment["NUGET_HTTP_CACHE_PATH"] = nugetHttpCache;
            buildStartInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
            foreach (var argument in new[]
            {
                "build",
                project,
                $"--property:McgIsolatedArtifactsPath={artifactsPath}",
                "--property:BuildInParallel=false",
                "--verbosity",
                "quiet"
            })
            {
                buildStartInfo.ArgumentList.Add(argument);
            }

            var buildResult = MtpTestRunnerScriptTests.Run(
                buildStartInfo,
                timeout: MtpTestRunnerScriptTests.NativeMtpRealProcessHangGuard);
            Xunit.Assert.True(
                buildResult.ExitCode == 0,
                $"dotnet build {project} exited {buildResult.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{buildResult.Stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{buildResult.Stderr}");

            var projectName = Path.GetFileNameWithoutExtension(project);
            var buildOutput = Path.Combine(artifactsPath, "bin", projectName, "debug");
            var managedAssembly = Path.Combine(buildOutput, $"{projectName}.dll");
            var appHost = Path.Combine(
                buildOutput,
                OperatingSystem.IsWindows() ? $"{projectName}.exe" : projectName);
            Xunit.Assert.True(File.Exists(appHost), $"Expected built MTP apphost '{appHost}'.");
            Xunit.Assert.True(File.Exists(managedAssembly), $"Expected built MTP assembly '{managedAssembly}'.");

            var testStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            testStartInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
            foreach (var argument in new[]
            {
                managedAssembly,
                "--filter-class",
                $"*{testClass}*",
                "--minimum-expected-tests",
                "1",
                "--no-ansi",
                "--progress",
                "off"
            })
            {
                testStartInfo.ArgumentList.Add(argument);
            }

            Xunit.Assert.Equal(managedAssembly, testStartInfo.ArgumentList[0]);
            var result = MtpTestRunnerScriptTests.Run(
                testStartInfo,
                timeout: MtpTestRunnerScriptTests.NativeMtpRealProcessHangGuard);

            Xunit.Assert.True(
                result.ExitCode == 0,
                $"dotnet {managedAssembly} exited {result.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{result.Stderr}");
        }
        finally
        {
            lease.Dispose();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }
}
