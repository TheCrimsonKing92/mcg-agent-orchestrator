using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerFocusedRunner)]
public sealed class DotnetBuildEnvironmentManagerTestsLocalHostOnlyFocusedRunner : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Xunit.Trait("Category", "LocalHostOnly")]
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

            var result = TestChildProcessCapture.Run(startInfo, TimeSpan.FromSeconds(60));
            var stdout = result.Stdout;
            var stderr = result.Stderr;
            Assert.True(
                result.ExitCode == 0,
                $"Focused invocation exited {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

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
            Assert.True(
                childProcess.GetProperty("identityStatus").GetString() == "confirmed",
                $"Focused runner child identity was not confirmed. Retained child-process receipt: {childProcess.GetRawText()}");
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
}
