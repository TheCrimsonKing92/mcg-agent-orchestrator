using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsFocusedRunner
{
    [Xunit.Fact]
    public void FocusedRunner_TestFailureSurvivesWorktreeDrift()
    {
        var source = ReadIsolatedDotnetScript();
        var driftGuard = source.IndexOf("-not $receipt.worktreeStateAfter.unchanged", StringComparison.Ordinal);
        var preserveFailure = source.IndexOf(
            "-not ($receipt.outcome -eq \"FAIL\" -and $receipt.reason -eq \"test-failures\")",
            driftGuard,
            StringComparison.Ordinal);
        var blockedAssignment = source.IndexOf("$receipt.outcome = \"BLOCKED\"", driftGuard, StringComparison.Ordinal);

        Assert.True(driftGuard >= 0, "Focused runner must detect post-run worktree drift.");
        Assert.True(preserveFailure > driftGuard, "A TRX-established test failure must be excluded from drift demotion.");
        Assert.True(preserveFailure < blockedAssignment, "The test-failure exclusion must guard the BLOCKED assignment.");
    }

    [Xunit.Fact]
    public void FocusedRunner_InvalidMethodToken_RemainsPreLease()
    {
        var source = ReadIsolatedDotnetScript();
        var validation = source.IndexOf("if ($FocusedTest) {", StringComparison.Ordinal);
        var repositoryRoot = source.IndexOf("$RepositoryRoot = (Get-Location).Path", StringComparison.Ordinal);
        var strictGrammar = source.IndexOf(
            @"\AFullyQualifiedName~[A-Za-z_][A-Za-z0-9_]{7,}(?:\|FullyQualifiedName~[A-Za-z_][A-Za-z0-9_]{7,})*\z",
            StringComparison.Ordinal);

        Assert.True(validation >= 0 && strictGrammar > validation && strictGrammar < repositoryRoot);
        Assert.DoesNotContain("[A-Za-z0-9_.]*", source[validation..repositoryRoot], StringComparison.Ordinal);
        Assert.Contains("exit 4", source[validation..repositoryRoot], StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FocusedRunner_WorkerGrant_UsesPersistedDeveloperRole()
    {
        var source = ReadIsolatedDotnetScript();
        var mode = FocusedModeSource(source);
        var authorization = mode.IndexOf("Get-FocusedWorkerAuthorization", StringComparison.Ordinal);
        var lease = mode.IndexOf("Enter-FocusedBuildSlot", StringComparison.Ordinal);

        Assert.Contains("json_each(goals.snapshot_json, '$.Tasks')", source, StringComparison.Ordinal);
        Assert.Contains("json_extract(task.value, '$.RequiredRole')", source, StringComparison.Ordinal);
        Assert.Contains("[string]::Equals($role, 'Developer'", source, StringComparison.Ordinal);
        Assert.True(authorization >= 0 && lease > authorization);
    }

    [Xunit.Fact]
    public void FocusedRunner_ReceiptNamesExecutedIsolatedCommand()
    {
        var source = ReadIsolatedDotnetScript();
        var mode = FocusedModeSource(source);
        var arguments = mode.IndexOf("$testArguments = @(", StringComparison.Ordinal);
        var receipt = mode.IndexOf("$receipt.testArguments = @($testProcessArguments)", StringComparison.Ordinal);
        var invocation = mode.IndexOf(
            "Invoke-FocusedChildProcess -FileName \"dotnet\" -ProcessArguments $testProcessArguments",
            StringComparison.Ordinal);

        Assert.Contains(@"Join-Path $runRoot ""focused-artifacts\$($slotLease.Id)""", mode, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Join-Path $runRoot ""artifacts""", mode, StringComparison.Ordinal);
        Assert.Contains("Get-FocusedMtpFilterArguments -Filter $FocusedTestFilter", mode, StringComparison.Ordinal);
        Assert.True(arguments >= 0 && receipt > arguments && invocation > receipt);
    }

    [Xunit.Fact]
    public void FocusedRunner_FreshnessIncludesUntrackedContent()
    {
        var source = ReadIsolatedDotnetScript();
        var stateStart = source.IndexOf("function Get-FocusedWorktreeState", StringComparison.Ordinal);
        var stateEnd = source.IndexOf("function ConvertTo-DeclaredMutationPath", stateStart, StringComparison.Ordinal);
        var freshnessStart = source.IndexOf("function Test-FocusedBuildIsCurrent", StringComparison.Ordinal);
        var freshnessEnd = source.IndexOf("function Write-FocusedReceipt", freshnessStart, StringComparison.Ordinal);
        var state = source[stateStart..stateEnd];
        var freshness = source[freshnessStart..freshnessEnd];

        Assert.Contains("ls-files --others --exclude-standard", state, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $path -Algorithm SHA256", state, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Item", freshness, StringComparison.Ordinal);
        Assert.DoesNotContain("ls-files", freshness, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FocusedRunner_LeaseHeartbeatLeavesGateDeadlineMargin()
    {
        var source = ReadIsolatedDotnetScript();
        var mode = FocusedModeSource(source);

        Assert.Contains("activity-$slot.heartbeat.json", source, StringComparison.Ordinal);
        Assert.Contains("lastObservedAt", source, StringComparison.Ordinal);
        Assert.Contains("lastProgressAt", source, StringComparison.Ordinal);
        Assert.Contains("state = \"running\"", source, StringComparison.Ordinal);
        Assert.Contains("$runDeadline.AddSeconds(-$cleanupMarginSeconds)", mode, StringComparison.Ordinal);
        Assert.Contains("$leaseDeadline = [DateTime]::UtcNow.AddSeconds($FocusedLeaseWaitSeconds)", mode, StringComparison.Ordinal);
        Assert.Contains("if ($leaseDeadline -gt $processDeadline)", mode, StringComparison.Ordinal);
        Assert.Contains("$leaseDeadline = $processDeadline", mode, StringComparison.Ordinal);
        Assert.Contains("if ([DateTime]::UtcNow -ge $processDeadline)", mode, StringComparison.Ordinal);
        Assert.Contains("$receipt.reason = \"budget-exceeded\"", mode, StringComparison.Ordinal);
        Assert.Contains("-HeartbeatPath $slotLease.HeartbeatPath", mode, StringComparison.Ordinal);
        Assert.Contains("$path.acceptance-priority.lock", source, StringComparison.Ordinal);
        Assert.Contains("$acceptancePriorityStream.Lock(0, 1)", source, StringComparison.Ordinal);
        Assert.Contains("& dotnet build-server shutdown", mode, StringComparison.Ordinal);
        Assert.Contains("$slotLease.Stream.Dispose()", mode, StringComparison.Ordinal);
        Assert.Contains("Write-FocusedReceipt -Receipt $receipt", mode, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FocusedRunner_UnderspecifiedFilter_ExitsInvalidBeforeLeaseOrDotnet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        try
        {
            var logPath = Path.Combine(root, "dotnet.log");
            File.WriteAllText(
                Path.Combine(shimDirectory, "dotnet.cmd"),
                "@echo off\r\n>> \"%DOTNET_SHIM_LOG%\" echo args=%*\r\nexit /b 0\r\n");
            var isolatedRoot = Path.Combine(root, "isolated-dotnet");
            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-FocusedTest");
            startInfo.ArgumentList.Add("-TestFilter");
            startInfo.ArgumentList.Add("FullyQualifiedName~T");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Tests.csproj");
            startInfo.Environment["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_SHIM_LOG"] = logPath;
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = isolatedRoot;
            startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "Focused invalid-filter invocation did not exit within 10 seconds.");

            Assert.Equal(4, process.ExitCode);
            using var receipt = JsonDocument.Parse(stdout);
            Assert.Equal("INVALID", receipt.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("invalid-focused-request", receipt.RootElement.GetProperty("reason").GetString());
            Assert.False(File.Exists(logPath));
            Assert.False(Directory.Exists(isolatedRoot));
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var sharedProfileRoot = Path.Combine(
            ResolveSharedLocalAppData(),
            "Temp",
            "Low",
            "f",
            Guid.NewGuid().ToString("N")[..8]);
        var localAppData = Path.Combine(sharedProfileRoot, "Local");
        var nestedIsolatedRoot = Path.GetFullPath(Path.Combine(
            localAppData,
            "..",
            "LocalLow",
            DotnetBuildEnvironmentManager.RootDirectoryName));
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
            Directory.CreateDirectory(shimDirectory);
            Directory.CreateDirectory(workDirectory);
            Directory.CreateDirectory(Path.Combine(workDirectory, ".mcg-sandbox", "temp"));
        try
        {
            const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
            var projectFile = $"{projectName}.csproj";
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Focused Runner Test");
            File.WriteAllText(Path.Combine(workDirectory, projectFile), "<Project />");
            RunCommand("git", workDirectory, "add", projectFile);
            RunCommand("git", workDirectory, "commit", "-m", "base");
            File.AppendAllText(Path.Combine(workDirectory, ".git", "info", "exclude"), $"{Environment.NewLine}.orchestrator/{Environment.NewLine}.mcg-sandbox/{Environment.NewLine}");
            var commit = RunCommand("git", workDirectory, "rev-parse", "HEAD").Trim();

            var logPath = Path.Combine(root, "dotnet.log");
            var receiptPath = Path.Combine(root, "focused.receipt.json");
            var testOutput = Path.GetDirectoryName(typeof(DotnetBuildEnvironmentManagerTests).Assembly.Location)!;
            var isolatedRoot = nestedIsolatedRoot;
            var slot = "aaaaaaaa".Aggregate(
                0,
                (hash, character) => (hash + character) % DotnetBuildEnvironmentManager.BuildConcurrencySlotCount);
            var artifactsPath = Path.Combine(
                isolatedRoot,
                "goals",
                "aaaaaaaa",
                "focused-artifacts",
                $"build-{slot}");
            var artifactOutput = Path.Combine(artifactsPath, "bin", projectName, "debug");
            foreach (var sourcePath in Directory.GetFiles(testOutput, "*", SearchOption.AllDirectories))
            {
                var targetPath = Path.Combine(artifactOutput, Path.GetRelativePath(testOutput, sourcePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath);
            }
            File.WriteAllText(
                Path.Combine(artifactsPath, ".mcg-artifacts-owner.json"),
                JsonSerializer.Serialize(new { ownerToken = $"focused-aaaaaaaa-build-{slot}" }));
            var cleanDigest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
            var fingerprintInput = string.Join(
                '\n',
                commit,
                cleanDigest,
                Path.GetFullPath(Path.Combine(workDirectory, projectFile)),
                "Debug",
                string.Empty);
            var fingerprint = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(artifactsPath, ".mcg-focused-build-state.json"),
                JsonSerializer.Serialize(new { fingerprint }));
            File.WriteAllText(
                Path.Combine(shimDirectory, "dotnet.cmd"),
                """
                @echo off
                >> "%DOTNET_SHIM_LOG%" echo args=%*
                if "%~1"=="build-server" exit /b 0
                exit /b 0
                """);

            const string goalId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string taskId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var logDirectory = Path.Combine(workDirectory, ".orchestrator", "logs");
            Directory.CreateDirectory(logDirectory);
            var startGatePath = Path.Combine(logDirectory, "focused.start-gate");
            File.WriteAllText(
                Path.Combine(logDirectory, "focused.dispatch.json"),
                JsonSerializer.Serialize(new { prepGoalId = goalId, prepTaskId = taskId, workingDirectory = workDirectory }));
            var statePath = Path.Combine(workDirectory, ".orchestrator", "state.db");
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={statePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE goals (id TEXT PRIMARY KEY, snapshot_json TEXT NOT NULL); INSERT INTO goals VALUES ($goal, $snapshot);";
                command.Parameters.AddWithValue("$goal", goalId);
                command.Parameters.AddWithValue(
                    "$snapshot",
                    JsonSerializer.Serialize(new { Tasks = new[] { new { Id = taskId, RequiredRole = "Developer" } } }));
                command.ExecuteNonQuery();
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-FocusedTest");
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("aaaaaaaa");
            startInfo.ArgumentList.Add("-TestFilter");
            startInfo.ArgumentList.Add("FullyQualifiedName~AcceptanceCriterionFeasibilityTests");
            startInfo.ArgumentList.Add("-ReceiptPath");
            startInfo.ArgumentList.Add(receiptPath);
            startInfo.ArgumentList.Add("-BudgetSeconds");
            startInfo.ArgumentList.Add("60");
            startInfo.ArgumentList.Add("-LeaseWaitSeconds");
            startInfo.ArgumentList.Add("5");
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add(projectFile);
            startInfo.Environment["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_SHIM_LOG"] = logPath;
            startInfo.Environment.Remove(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
            startInfo.Environment["LOCALAPPDATA"] = localAppData;
            startInfo.Environment["TEMP"] = Path.Combine(workDirectory, ".mcg-sandbox", "temp");
            startInfo.Environment["TMP"] = Path.Combine(workDirectory, ".mcg-sandbox", "temp");
            startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workDirectory;
            startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = "1";
            startInfo.Environment[DispatchProcessHost.StartGatePathVariable] = startGatePath;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60000), "Focused PASS invocation did not exit within 60 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Focused invocation exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
            var rootElement = receipt.RootElement;
            Assert.Equal("PASS", rootElement.GetProperty("outcome").GetString());
            Assert.Equal(0, rootElement.GetProperty("exitCode").GetInt32());
            Assert.True(rootElement.GetProperty("total").GetInt32() > 0);
            Assert.True(rootElement.GetProperty("leaseReleased").GetBoolean());
            Assert.True(rootElement.GetProperty("worktreeStateAfter").GetProperty("unchanged").GetBoolean());
            Assert.True(rootElement.GetProperty("buildReused").GetBoolean());
            Assert.Equal("Developer", rootElement.GetProperty("authorization").GetProperty("role").GetString());
            Assert.Equal("dotnet", rootElement.GetProperty("testExecutable").GetString());
            Assert.Equal(0, rootElement.GetProperty("testProcessExitCode").GetInt32());
            var childProcess = rootElement.GetProperty("childProcess");
            Assert.Equal("confirmed", childProcess.GetProperty("identityStatus").GetString());
            Assert.Equal(
                "dotnet.exe",
                Path.GetFileName(childProcess.GetProperty("executable").GetString()),
                ignoreCase: true);
            var executedArguments = rootElement.GetProperty("testArguments")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            var expectedAssemblyPath = Path.Combine(artifactOutput, $"{projectName}.dll");
            Assert.Equal(expectedAssemblyPath, executedArguments[0], ignoreCase: true);
            Assert.Contains(
                executedArguments,
                value => value == "*AcceptanceCriterionFeasibilityTests*");
            Assert.False(File.Exists(logPath));
            Assert.True(string.IsNullOrWhiteSpace(RunCommand("git", workDirectory, "status", "--porcelain")));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort when antivirus briefly retains a copied test dependency.
            }
            try
            {
                Directory.Delete(nestedIsolatedRoot, recursive: true);
            }
            catch
            {
                // Best effort when antivirus briefly retains a copied test dependency.
            }
            try
            {
                Directory.Delete(sharedProfileRoot, recursive: true);
            }
            catch
            {
                // Best effort when antivirus briefly retains a copied test dependency.
            }
        }
    }

    [Xunit.Fact]
    public void FocusedRunner_BudgetExceeded_KillsBuildTreeAndDoesNotRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var workDirectory = Path.Combine(root, "repo");
        var shimDirectory = Path.Combine(root, "shim");
        var sharedRoot = Path.Combine(
            ResolveSharedLocalAppData(),
            "Temp", "Low", "f", Guid.NewGuid().ToString("N")[..8]);
        var isolatedRoot = Path.Combine(sharedRoot, "isolated");
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(shimDirectory);
        try
        {
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Focused Runner Test");
            const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
            var projectFile = $"{projectName}.csproj";
            File.WriteAllText(Path.Combine(workDirectory, projectFile), "<Project />");
            RunCommand("git", workDirectory, "add", projectFile);
            RunCommand("git", workDirectory, "commit", "-m", "base");
            var logPath = Path.Combine(root, "dotnet.log");
            var receiptPath = Path.Combine(root, "budget.receipt.json");
            var startedPath = Path.Combine(root, "started");
            PrepareFocusedArtifacts(workDirectory, projectFile, projectName, "budget-test", isolatedRoot);

            var startInfo = CreateFocusedStartInfo(
                scriptPath,
                workDirectory,
                shimDirectory,
                isolatedRoot,
                receiptPath,
                logPath,
                "budget-test",
                budgetSeconds: 15,
                leaseWaitSeconds: 5,
                projectFile,
                "FullyQualifiedName~FocusedProcessFixtureTests");
            startInfo.Environment["FOCUSED_STARTED_MARKER"] = startedPath;
            startInfo.Environment["FOCUSED_RELEASE"] = Path.Combine(root, "never-release");
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start budget fixture.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30000), "Budget fixture did not exit within 30 seconds.");
            var result = (ExitCode: process.ExitCode, Stdout: stdout, Stderr: stderr);

            var diagnostics = BuildFocusedRunnerFailureDiagnostics(result, receiptPath, isolatedRoot);
            if (result.ExitCode != 2)
            {
                Assert.Fail(diagnostics);
            }
            Assert.Equal(2, result.ExitCode);
            Assert.True(
                File.Exists(startedPath),
                "The focused child must execute the marker-writing test before the budget control terminates its tree.");
            Assert.True(File.Exists(receiptPath), diagnostics);
            using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
            Assert.Equal("BLOCKED", receipt.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(2, receipt.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("budget-exceeded", receipt.RootElement.GetProperty("reason").GetString());
            Assert.True(receipt.RootElement.GetProperty("leaseReleased").GetBoolean());
            Assert.Equal("released", receipt.RootElement.GetProperty("leaseState").GetString());
            Assert.True(receipt.RootElement.GetProperty("worktreeStateAfter").GetProperty("unchanged").GetBoolean());
            var childProcess = receipt.RootElement.GetProperty("childProcess");
            Assert.True(childProcess.GetProperty("processId").GetInt32() > 0);
            var identityStatus = childProcess.GetProperty("identityStatus").GetString();
            Assert.Equal("confirmed", identityStatus);
            Assert.False(string.IsNullOrWhiteSpace(childProcess.GetProperty("startedAt").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(childProcess.GetProperty("executable").GetString()));
            Assert.True(childProcess.GetProperty("terminationRequested").GetBoolean());
            Assert.True(childProcess.GetProperty("terminationSucceeded").GetBoolean());
            Assert.Equal("exited", childProcess.GetProperty("stateAfter").GetString());
            Assert.Equal("exited", GetFocusedChildIdentityState(childProcess));
            var slotPath = Path.Combine(isolatedRoot, "build-slots", receipt.RootElement.GetProperty("slotId").GetString() + ".lock");
            Assert.False(IsByteRangeLocked(slotPath));
            var outputLogPath = receipt.RootElement.GetProperty("outputLogPath").GetString();
            Assert.False(string.IsNullOrWhiteSpace(outputLogPath));
            Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("testProcessExitCode").ValueKind);
            Assert.False(File.Exists(logPath));
            Assert.True(string.IsNullOrWhiteSpace(RunCommand("git", workDirectory, "status", "--porcelain")));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort when Git object files retain read-only attributes on Windows.
            }
            try
            {
                Directory.Delete(sharedRoot, recursive: true);
            }
            catch
            {
                // Best effort when a killed fixture briefly retains an image handle.
            }
        }
    }

    [Xunit.Fact]
    public void FocusedChildProcess_AcceptancePriority_KillsDescendantTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var harnessPath = Path.Combine(root, "focused-child-harness.ps1");
        var fixturePath = Path.Combine(root, "focused-child-fixture.ps1");
        var resultPath = Path.Combine(root, "focused-child-result.json");
        var descendantIdentityPath = Path.Combine(root, "descendant.json");
        var releasePath = Path.Combine(root, "never-release");
        var priorityPath = Path.Combine(root, "acceptance-priority.lock");
        Process? harness = null;
        try
        {
            var source = ReadIsolatedDotnetScript();
            var functionStart = source.IndexOf("function Invoke-FocusedChildProcess", StringComparison.Ordinal);
            var functionEnd = source.IndexOf("function Set-FocusedChildProcessReceipt", functionStart, StringComparison.Ordinal);
            Assert.True(functionStart >= 0 && functionEnd > functionStart);
            var focusedChildFunction = source[functionStart..functionEnd];

            File.WriteAllText(
                fixturePath,
                """
                Set-StrictMode -Version Latest
                $ErrorActionPreference = "Stop"
                $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = $env:FOCUSED_TREE_SHELL
                $startInfo.UseShellExecute = $false
                $startInfo.CreateNoWindow = $true
                $startInfo.RedirectStandardOutput = $true
                $startInfo.RedirectStandardError = $true
                foreach ($argument in @(
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    'while (-not (Test-Path -LiteralPath $env:FOCUSED_TREE_RELEASE)) { Start-Sleep -Milliseconds 100 }')) {
                    [void]$startInfo.ArgumentList.Add($argument)
                }
                $descendant = [System.Diagnostics.Process]::Start($startInfo)
                if ($null -eq $descendant) {
                    throw "Failed to start the focused child descendant fixture."
                }
                $descendantExecutable = $descendant.MainModule.FileName
                [ordered]@{
                    processId = $descendant.Id
                    parentProcessId = $PID
                    startedAt = $descendant.StartTime.ToUniversalTime().ToString('o')
                    executable = $descendantExecutable
                    identityStatus = "confirmed"
                } | ConvertTo-Json | Set-Content -LiteralPath $env:FOCUSED_TREE_IDENTITY
                while (-not (Test-Path -LiteralPath $env:FOCUSED_TREE_RELEASE)) {
                    Start-Sleep -Milliseconds 100
                }
                """);
            File.WriteAllText(
                harnessPath,
                $$"""
                Set-StrictMode -Version Latest
                $ErrorActionPreference = "Stop"
                $script:RepositoryRoot = $env:FOCUSED_TREE_ROOT
                {{focusedChildFunction}}
                $result = Invoke-FocusedChildProcess `
                    -FileName $env:FOCUSED_TREE_SHELL `
                    -ProcessArguments @("-NoProfile", "-NonInteractive", "-File", $env:FOCUSED_TREE_FIXTURE) `
                    -Deadline ([DateTime]::UtcNow.AddSeconds(30)) `
                    -HeartbeatPath "" `
                    -AcceptancePriorityPath $env:FOCUSED_TREE_PRIORITY
                $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:FOCUSED_TREE_RESULT
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                harnessPath
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["FOCUSED_TREE_ROOT"] = root;
            startInfo.Environment["FOCUSED_TREE_SHELL"] = WorkerShell.Executable;
            startInfo.Environment["FOCUSED_TREE_FIXTURE"] = fixturePath;
            startInfo.Environment["FOCUSED_TREE_IDENTITY"] = descendantIdentityPath;
            startInfo.Environment["FOCUSED_TREE_RELEASE"] = releasePath;
            startInfo.Environment["FOCUSED_TREE_PRIORITY"] = priorityPath;
            startInfo.Environment["FOCUSED_TREE_RESULT"] = resultPath;

            harness = Process.Start(startInfo) ??
                throw new InvalidOperationException("Failed to start the focused child-process harness.");
            JsonDocument? publishedDescendantIdentity = null;
            Assert.True(
                SpinWait.SpinUntil(
                    () => TryReadJsonDocument(descendantIdentityPath, out publishedDescendantIdentity),
                    TimeSpan.FromSeconds(15)),
                "The focused child fixture did not publish a readable descendant identity.");
            using var descendantIdentity = publishedDescendantIdentity!;
            Assert.Equal("confirmed", descendantIdentity.RootElement.GetProperty("identityStatus").GetString());
            Assert.Equal("running-same-identity", GetFocusedChildIdentityState(descendantIdentity.RootElement));
            using var descendantProcess = Process.GetProcessById(
                descendantIdentity.RootElement.GetProperty("processId").GetInt32());
            using (var priority = File.Open(priorityPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var priorityLocked = false;
                Assert.True(
                    SpinWait.SpinUntil(
                        () =>
                        {
                            try
                            {
                                priority.Lock(0, 1);
                                priorityLocked = true;
                                return true;
                            }
                            catch (IOException)
                            {
                                return false;
                            }
                        },
                        TimeSpan.FromSeconds(5)),
                    "The acceptance-priority probe lock was not acquired.");
                try
                {
                    Assert.True(harness.WaitForExit(10000), "The focused child harness did not honor acceptance priority.");
                }
                finally
                {
                    if (priorityLocked)
                    {
                        priority.Unlock(0, 1);
                    }
                }
            }

            var stdout = harness.StandardOutput.ReadToEnd();
            var stderr = harness.StandardError.ReadToEnd();
            var diagnostics = $"harnessExitCode={harness.ExitCode}{Environment.NewLine}stdout={stdout}{Environment.NewLine}stderr={stderr}";
            if (harness.ExitCode != 0)
            {
                Assert.Fail(diagnostics);
            }
            Assert.Equal(0, harness.ExitCode);
            Assert.True(File.Exists(resultPath), diagnostics);
            using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
            Assert.False(result.RootElement.GetProperty("TimedOut").GetBoolean());
            Assert.True(result.RootElement.GetProperty("AcceptancePriorityRequested").GetBoolean());
            Assert.True(result.RootElement.GetProperty("TerminationRequested").GetBoolean());
            Assert.True(result.RootElement.GetProperty("TerminationSucceeded").GetBoolean());
            Assert.Equal("exited", result.RootElement.GetProperty("ProcessStateAfter").GetString());

            Assert.True(descendantProcess.WaitForExit(5000), diagnostics);
            Assert.Equal(
                result.RootElement.GetProperty("ChildProcessId").GetInt32(),
                descendantIdentity.RootElement.GetProperty("parentProcessId").GetInt32());
            Assert.NotEqual(
                result.RootElement.GetProperty("ChildProcessId").GetInt32(),
                descendantIdentity.RootElement.GetProperty("processId").GetInt32());
            Assert.Equal("exited", GetFocusedChildIdentityState(descendantIdentity.RootElement));
        }
        finally
        {
            if (harness is not null)
            {
                StopProcess(harness);
                harness.Dispose();
            }
            StopFocusedProcessIdentity(descendantIdentityPath);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort when a killed fixture briefly retains an image handle.
            }
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(2, "budget-exceeded", "exit-2-budget-exceeded", "parsed")]
    [Xunit.InlineData(3, "acceptance-priority", "exit-3-distinct-runner-failure", "parsed")]
    [Xunit.InlineData(5, "no-tests", "exit-5-harness-setup-or-no-tests", "parsed")]
    [Xunit.InlineData(5, "missing", "exit-5-harness-setup-or-no-tests", "missing")]
    [Xunit.InlineData(5, "malformed", "exit-5-harness-setup-or-no-tests", "malformed")]
    public void FocusedRunner_FailureDiagnostics_ClassifyTypedOutcomes(
        int exitCode,
        string receiptReason,
        string expectedClassification,
        string expectedReceiptStatus)
    {
        var root = CreateTempDirectory();
        try
        {
            var receiptPath = Path.Combine(root, "focused.receipt.json");
            if (receiptReason == "malformed")
            {
                File.WriteAllText(receiptPath, "{");
            }
            else if (receiptReason != "missing")
            {
                File.WriteAllText(
                    receiptPath,
                    JsonSerializer.Serialize(new
                    {
                        outcome = "BLOCKED",
                        exitCode,
                        reason = receiptReason,
                        leaseReleased = false,
                        leaseState = "not-acquired",
                        slotId = (string?)null,
                        childProcess = new
                        {
                            processId = (int?)null,
                            startedAt = (string?)null,
                            executable = (string?)null,
                            identityStatus = "not-started",
                            identityError = (string?)null,
                            terminationRequested = false,
                            terminationSucceeded = (bool?)null,
                            terminationError = (string?)null,
                            stateAfter = "not-started"
                        }
                    }));
            }

            var diagnostics = BuildFocusedRunnerFailureDiagnostics(
                (exitCode, "outer-stdout", "outer-stderr"),
                receiptPath,
                root);

            Assert.Contains($"exitClassification={expectedClassification}", diagnostics, StringComparison.Ordinal);
            Assert.Contains($"receiptStatus={expectedReceiptStatus}", diagnostics, StringComparison.Ordinal);
            Assert.Contains("stdout=outer-stdout", diagnostics, StringComparison.Ordinal);
            Assert.Contains("stderr=outer-stderr", diagnostics, StringComparison.Ordinal);
            if (expectedReceiptStatus == "parsed")
            {
                Assert.Contains($"receiptReason={receiptReason}", diagnostics, StringComparison.Ordinal);
                Assert.Contains($"receiptExitCode={exitCode}", diagnostics, StringComparison.Ordinal);
                Assert.Contains("receiptOutcome=BLOCKED", diagnostics, StringComparison.Ordinal);
                Assert.Contains("leaseState=not-acquired", diagnostics, StringComparison.Ordinal);
                Assert.Contains("ownedProcessState=not-started", diagnostics, StringComparison.Ordinal);
                Assert.Contains("liveDescendantInspectionStatus=not-started", diagnostics, StringComparison.Ordinal);
                Assert.Contains("childIdentityError=<null>", diagnostics, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FocusedRunner_LiveDescendantDiagnostics_ReportInspectionFailure()
    {
        using var receipt = JsonDocument.Parse("""
            {
              "processId": 123,
              "identityStatus": "confirmed"
            }
            """);

        var descendants = TryGetFocusedLiveDescendantProcessIds(
            receipt.RootElement,
            out var inspectionStatus,
            _ => throw new InvalidOperationException("inspection failed"));

        Assert.Empty(descendants);
        Assert.Equal("inspection-failed:InvalidOperationException", inspectionStatus);
    }

    [Xunit.Fact]
    public void FocusedRunner_LiveDescendantDiagnostics_DoNotInspectUnconfirmedPid()
    {
        using var receipt = JsonDocument.Parse("""
            {
              "processId": 123,
              "identityStatus": "pid-only",
              "identityError": "System.ComponentModel.Win32Exception: transient identity read failure"
            }
            """);
        var inspectionAttempted = false;

        var descendants = TryGetFocusedLiveDescendantProcessIds(
            receipt.RootElement,
            out var inspectionStatus,
            _ =>
            {
                inspectionAttempted = true;
                return [];
            });

        Assert.Empty(descendants);
        Assert.False(inspectionAttempted);
        Assert.Equal("identity-unconfirmed:pid-only", inspectionStatus);
        Assert.Equal("identity-unconfirmed:pid-only", GetFocusedChildIdentityState(receipt.RootElement));
    }

    [Xunit.Fact]
    public void FocusedRunner_AllSlotsHeld_ReportsNoSlotWithoutStartingDotnet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // This is a conservative setup/cleanup ceiling, not a measured setup-latency percentile.
        // The lease phase remains the one-second condition exercised by this fixture.
        const int totalBudgetSeconds = 8;
        const int leaseWaitSeconds = 1;

        var repoRoot = ResolveRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Invoke-IsolatedDotnet.ps1");
        var root = CreateTempDirectory();
        var workDirectory = Path.Combine(root, "repo");
        var shimDirectory = Path.Combine(root, "shim");
        var sharedRoot = Path.Combine(
            ResolveSharedLocalAppData(),
            "Temp", "Low", "f", Guid.NewGuid().ToString("N")[..8]);
        var isolatedRoot = Path.Combine(sharedRoot, "isolated");
        var heldSlots = new List<FileStream>();
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(Path.Combine(isolatedRoot, "build-slots"));
        try
        {
            RunCommand("git", workDirectory, "init", "--initial-branch=main");
            RunCommand("git", workDirectory, "config", "user.email", "test@example.invalid");
            RunCommand("git", workDirectory, "config", "user.name", "Focused Runner Test");
            const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
            var projectFile = $"{projectName}.csproj";
            File.WriteAllText(Path.Combine(workDirectory, projectFile), "<Project />");
            RunCommand("git", workDirectory, "add", projectFile);
            RunCommand("git", workDirectory, "commit", "-m", "base");
            for (var slot = 0; slot < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount; slot++)
            {
                var stream = new FileStream(
                    Path.Combine(isolatedRoot, "build-slots", $"build-{slot}.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);
                stream.Lock(0, 1);
                heldSlots.Add(stream);
            }

            var logPath = Path.Combine(root, "dotnet.log");
            var receiptPath = Path.Combine(root, "no-slot.receipt.json");
            File.WriteAllText(Path.Combine(shimDirectory, "dotnet.cmd"), "@echo off\r\n>> \"%DOTNET_SHIM_LOG%\" echo started\r\nexit /b 0\r\n");
            var result = RunFocusedScript(
                scriptPath,
                workDirectory,
                shimDirectory,
                isolatedRoot,
                receiptPath,
                logPath,
                "no-slot-test",
                budgetSeconds: totalBudgetSeconds,
                leaseWaitSeconds: leaseWaitSeconds,
                projectFile);

            Assert.True(result.ExitCode == 3, $"Focused no-slot invocation exited {result.ExitCode}.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
            using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
            Assert.Equal("BLOCKED", receipt.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(3, receipt.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("no-slot", receipt.RootElement.GetProperty("reason").GetString());
            Assert.Equal("not-acquired", receipt.RootElement.GetProperty("leaseState").GetString());
            Assert.False(receipt.RootElement.GetProperty("leaseReleased").GetBoolean());
            Assert.Equal("not-started", receipt.RootElement.GetProperty("childProcess").GetProperty("stateAfter").GetString());
            // This shim only observes PowerShell-resolved '& dotnet' calls. The receipt's
            // not-started child state above is the child-launch oracle for this fixture.
            Assert.False(File.Exists(logPath));
        }
        finally
        {
            foreach (var stream in heldSlots)
            {
                stream.Unlock(0, 1);
                stream.Dispose();
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort when Git object files retain read-only attributes on Windows.
            }
            try
            {
                Directory.Delete(sharedRoot, recursive: true);
            }
            catch
            {
                // Best effort when a fixture briefly retains a file handle.
            }
        }
    }

    [Xunit.Fact]
    public void AcceptanceLease_ReservesPriorityUntilFocusedSlotIsReleased()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(
            new GoalId("cccccccccccccccccccccccccccccccc"),
            "priority-test");
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        using var focusedLease = new FileStream(
            environment.ExecutionLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        focusedLease.Lock(0, 1);

        var acquisitionTask = Task.Run(() =>
            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.FromSeconds(5)));
        var priorityPath = environment.ExecutionLockPath + ".acceptance-priority.lock";
        Assert.True(SpinWait.SpinUntil(() => IsByteRangeLocked(priorityPath), TimeSpan.FromSeconds(5)));

        focusedLease.Unlock(0, 1);
        var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisitionTask.GetAwaiter().GetResult());
        acquired.Lease.Dispose();
    }

    [Xunit.Fact]
    public void FocusedRunner_ConcurrentInvocations_NeverExceedSharedGrid()
    {
        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var source = ReadIsolatedDotnetScript();
        Assert.Contains("Join-Path $lockDirectory \"build-$slot.lock\"", source, StringComparison.Ordinal);

        var participantCount = DotnetBuildEnvironmentManager.BuildConcurrencySlotCount + 1;
        using var start = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var acquired = new CountdownEvent(participantCount);
        var results = new System.Collections.Concurrent.ConcurrentBag<DotnetBuildLeaseAcquisition>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, participantCount).Select(index => Task.Factory.StartNew(() =>
        {
            var signaled = false;
            try
            {
                var environment = DotnetBuildEnvironmentManager.CreateAttempt(
                    new GoalId((index + 1).ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + new string('0', 24)),
                    $"focused-concurrency-{index}");
                start.Wait();
                var result = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero);
                results.Add(result);
                acquired.Signal();
                signaled = true;
                if (result is DotnetBuildLeaseAcquisition.Acquired held)
                {
                    release.Wait();
                    held.Lease.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
            finally
            {
                if (!signaled)
                {
                    acquired.Signal();
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        start.Set();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        var leases = results.OfType<DotnetBuildLeaseAcquisition.Acquired>().ToArray();
        Assert.Equal(DotnetBuildEnvironmentManager.BuildConcurrencySlotCount, leases.Length);
        Assert.Single(results.OfType<DotnetBuildLeaseAcquisition.SlotsBusy>());
        Assert.Equal(leases.Length, leases.Select(item => item.Lease.Environment.ExecutionLockPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(leases, item => Assert.True(IsByteRangeLocked(item.Lease.Environment.ExecutionLockPath)));

        release.Set();
        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)));
    }

}
