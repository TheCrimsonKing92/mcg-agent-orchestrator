using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsSqliteResolution
{
    private const string GoalId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TaskId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Xunit.Fact]
    public void PathOrder_FirstHitReviewer_RecordsReviewerAndChosenPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var reviewer = WriteSqliteShim(root, "A", "Reviewer");
            var tester = WriteSqliteShim(root, "B", "Tester");

            var (_, receipt) = RunFocusedScript(root, reviewer, tester);

            AssertRoleRefusal(receipt, "Reviewer");
            AssertChosenPath(receipt, reviewer);
            Assert.Single(File.ReadAllLines(Path.Combine(reviewer, "sqlite3.log")));
            Assert.False(File.Exists(Path.Combine(tester, "sqlite3.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void PathOrder_FirstHitTester_RecordsTesterAndChosenPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var reviewer = WriteSqliteShim(root, "A", "Reviewer");
            var tester = WriteSqliteShim(root, "B", "Tester");

            var (_, receipt) = RunFocusedScript(root, tester, reviewer);

            AssertRoleRefusal(receipt, "Tester");
            AssertChosenPath(receipt, tester);
            Assert.Single(File.ReadAllLines(Path.Combine(tester, "sqlite3.log")));
            Assert.False(File.Exists(Path.Combine(reviewer, "sqlite3.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FirstHitFailure_KeepsCatchAllReasonAndNamesChosenPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var failing = WriteSqliteShim(root, "first", role: null);
            var developer = WriteSqliteShim(root, "second", "Developer");

            var (_, receipt) = RunFocusedScript(root, failing, developer);

            Assert.Equal("focused-test-authorization-failed", receipt.GetProperty("reason").GetString());
            Assert.Equal("The canonical task role lookup failed.", receipt.GetProperty("error").GetString());
            AssertChosenPath(receipt, failing);
            Assert.Single(File.ReadAllLines(Path.Combine(failing, "sqlite3.log")));
            Assert.False(File.Exists(Path.Combine(developer, "sqlite3.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void SingleAndDoubleHit_ProduceEqualRoleReceipts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var tester = WriteSqliteShim(root, "first", "Tester");
            var reviewer = WriteSqliteShim(root, "second", "Reviewer");

            var single = RunFocusedScript(Path.Combine(root, "single"), tester);
            var multiple = RunFocusedScript(Path.Combine(root, "multiple"), tester, reviewer);

            AssertRoleRefusal(single.Receipt, "Tester");
            AssertRoleRefusal(multiple.Receipt, "Tester");
            Assert.Equal(single.ExitCode, multiple.ExitCode);
            foreach (var property in new[] { "outcome", "exitCode", "reason" })
            {
                Assert.Equal(single.Receipt.GetProperty(property).GetRawText(), multiple.Receipt.GetProperty(property).GetRawText());
            }
            foreach (var property in new[] { "goalId", "taskId", "role" })
            {
                Assert.Equal(
                    single.Receipt.GetProperty("authorization").GetProperty(property).GetString(),
                    multiple.Receipt.GetProperty("authorization").GetProperty(property).GetString());
            }
            Assert.Equal(2, File.ReadAllLines(Path.Combine(tester, "sqlite3.log")).Length);
            Assert.False(File.Exists(Path.Combine(reviewer, "sqlite3.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void NoSqliteOnPath_KeepsCatchAllReasonAndNullPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);

            var (_, receipt) = RunFocusedScript(root, empty);

            Assert.Equal("focused-test-authorization-failed", receipt.GetProperty("reason").GetString());
            Assert.Contains("sqlite3", receipt.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("sqliteExecutable").ValueKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-sqlite-resolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteSqliteShim(string root, string name, string? role)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "sqlite3.log");
        var response = role is null ? "exit /b 3\r\n" : $"echo {role}\r\nexit /b 0\r\n";
        File.WriteAllText(
            Path.Combine(directory, "sqlite3.cmd"),
            $"@echo off\r\n>> \"{logPath}\" echo invoked\r\n{response}");
        return directory;
    }

    private static (int ExitCode, JsonElement Receipt) RunFocusedScript(string root, params string[] shimDirectories)
    {
        var scriptPath = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "Invoke-IsolatedDotnet.ps1");
        var workDirectory = Path.Combine(root, "repo");
        var logDirectory = Path.Combine(workDirectory, ".orchestrator", "logs");
        Directory.CreateDirectory(logDirectory);
        var startGatePath = Path.Combine(logDirectory, "focused.start-gate");
        File.WriteAllText(
            Path.Combine(logDirectory, "focused.dispatch.json"),
            JsonSerializer.Serialize(new { prepGoalId = GoalId, prepTaskId = TaskId, workingDirectory = workDirectory }));
        File.WriteAllText(Path.Combine(workDirectory, ".orchestrator", "state.db"), string.Empty);

        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            "-FocusedTest", "-GoalPrefix", "aaaaaaaa", "-TestFilter",
            "FullyQualifiedName~AcceptanceCriterionFeasibilityTests", "-BudgetSeconds", "60",
            "-LeaseWaitSeconds", "5", "test", "Fake.Tests.csproj"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Keep shell startup inputs, while excluding ambient orchestrator behavior switches.
        startInfo.Environment.Clear();
        foreach (var variable in new[]
        {
            "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
            "PSModulePath", "TEMP", "TMP", "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)", "OS"
        })
        {
            if (Environment.GetEnvironmentVariable(variable) is { } value)
            {
                startInfo.Environment[variable] = value;
            }
        }
        // System32 keeps cmd.exe available without introducing any host-installed sqlite3 copies.
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, shimDirectories.Append(Environment.SystemDirectory));
        startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workDirectory;
        startInfo.Environment[WorkerSandboxOptions.DispatchWorkerVariable] = "1";
        startInfo.Environment[DispatchProcessHost.StartGatePathVariable] = startGatePath;
        startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, "isolated-dotnet");

        var result = TestChildProcessCapture.Run(startInfo, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 5, $"Focused invocation exited {result.ExitCode}. stdout: {result.Stdout} stderr: {result.Stderr}");
        Assert.Equal(string.Empty, result.Stderr);
        using var stdoutReceipt = JsonDocument.Parse(result.Stdout);
        var receipt = stdoutReceipt.RootElement;
        Assert.Equal("BLOCKED", receipt.GetProperty("outcome").GetString());
        Assert.Equal(5, receipt.GetProperty("exitCode").GetInt32());
        using var fileReceipt = JsonDocument.Parse(File.ReadAllText(receipt.GetProperty("receiptPath").GetString()!));
        Assert.True(JsonElement.DeepEquals(receipt, fileReceipt.RootElement));
        return (result.ExitCode, receipt.Clone());
    }

    private static void AssertRoleRefusal(JsonElement receipt, string role)
    {
        Assert.Equal("focused-test-role-not-authorized", receipt.GetProperty("reason").GetString());
        var authorization = receipt.GetProperty("authorization");
        Assert.Equal(GoalId, authorization.GetProperty("goalId").GetString());
        Assert.Equal(TaskId, authorization.GetProperty("taskId").GetString());
        Assert.Equal(role, authorization.GetProperty("role").GetString());
    }

    private static void AssertChosenPath(JsonElement receipt, string directory)
    {
        Assert.True(string.Equals(
            Path.Combine(directory, "sqlite3.cmd"),
            receipt.GetProperty("sqliteExecutable").GetString(),
            StringComparison.OrdinalIgnoreCase));
    }
}
