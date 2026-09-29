using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class GoalWorktreeTestsRemoveCleanupProcessProtection : GoalWorktreeTestBase
{

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete")]
    public void GoalWorktreesRemoveReapsRecordedWorkerProcessesBeforeDelete()
    {
        var repo = CreateSeededRepository();
        var originalKill = CleanupHooks.TryKillRecordedProcess;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Reap worker processes", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var startedAt = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(111, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [111, 222]));

            var killed = new List<int>();
            CleanupHooks.TryKillRecordedProcess = pid =>
            {
                killed.Add(pid);
                return true;
            };
            CleanupHooks.SandboxAclHelper = new RecordingSandboxAclHelper();
            CleanupHooks.BuildServerShutdown = (_, _) => { };

            var result = RemoveWorktree(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.True(killed.SequenceEqual([111, 222]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            CleanupHooks.TryKillRecordedProcess = originalKill;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_skips_protected_recorded_worker_process")]
    public async Task GoalWorktreesRemoveSkipsProtectedRecordedWorkerProcess()
    {
        await RunProcessProtectionCaseAsync(() =>
        {
            var repo = CreateSeededRepository();
            var originalKill = WorkerProcessJobs.TryKillPidTree;
            var originalAcl = CleanupHooks.SandboxAclHelper;
            var originalShutdown = CleanupHooks.BuildServerShutdown;
            var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
            var originalProtectedTicks = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedStartTicksVariable);
            try
            {
                using var protectedProcess = Process.GetCurrentProcess();
                var protectedPid = protectedProcess.Id;
                Environment.SetEnvironmentVariable(
                    CliProtectedProcessEnvironment.ProtectedPidVariable,
                    protectedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedStartTicksVariable,
                    protectedProcess.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var kernel = new AgentOrchestratorKernel();
                var goal = kernel.CreateGoal("Remove protected worker process", [
                    new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
                ]);
                kernel.ActivateGoal(goal.Id, EchoAgents());
                var task = goal.Tasks[0];
                var path = GoalWorktrees.WorktreePath(repo, goal.Id);
                Directory.CreateDirectory(path);
                var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
                kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
                kernel.RecordTaskProcessStarted(
                    goal.Id,
                    task.Id,
                    new TaskProcessRecord(protectedPid, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [protectedPid]));

                var killed = new List<int>();
                WorkerProcessJobs.TryKillPidTree = pid =>
                {
                    killed.Add(pid);
                    return true;
                };
                CleanupHooks.SandboxAclHelper = new RecordingSandboxAclHelper();
                CleanupHooks.BuildServerShutdown = (_, _) => { };

                var result = RemoveWorktree(repo, goal.Id, kernel);

                Assert.True(result.IsComplete);
                Assert.Empty(killed);
                Assert.False(Directory.Exists(path));
            }
            finally
            {
                WorkerProcessJobs.TryKillPidTree = originalKill;
                CleanupHooks.SandboxAclHelper = originalAcl;
                CleanupHooks.BuildServerShutdown = originalShutdown;
                Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
                Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedStartTicksVariable, originalProtectedTicks);
                DeleteDirectory(repo);
            }
        });
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_kills_unprotected_recorded_worker_process")]
    public async Task GoalWorktreesRemoveKillsUnprotectedRecordedWorkerProcess()
    {
        await RunProcessProtectionCaseAsync(() =>
        {
            var repo = CreateSeededRepository();
            var originalKill = WorkerProcessJobs.TryKillPidTree;
            var originalAcl = CleanupHooks.SandboxAclHelper;
            var originalShutdown = CleanupHooks.BuildServerShutdown;
            var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
            try
            {
                Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, "111");
                var kernel = new AgentOrchestratorKernel();
                var goal = kernel.CreateGoal("Remove unprotected worker process", [
                    new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
                ]);
                kernel.ActivateGoal(goal.Id, EchoAgents());
                var task = goal.Tasks[0];
                var path = GoalWorktrees.WorktreePath(repo, goal.Id);
                Directory.CreateDirectory(path);
                var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
                kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
                kernel.RecordTaskProcessStarted(
                    goal.Id,
                    task.Id,
                    new TaskProcessRecord(222, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [222]));

                var killed = new List<int>();
                WorkerProcessJobs.TryKillPidTree = pid =>
                {
                    killed.Add(pid);
                    return true;
                };
                CleanupHooks.SandboxAclHelper = new RecordingSandboxAclHelper();
                CleanupHooks.BuildServerShutdown = (_, _) => { };

                var result = RemoveWorktree(repo, goal.Id, kernel);

                Assert.True(result.IsComplete);
                Assert.True(killed.SequenceEqual([222]));
                Assert.False(Directory.Exists(path));
            }
            finally
            {
                WorkerProcessJobs.TryKillPidTree = originalKill;
                CleanupHooks.SandboxAclHelper = originalAcl;
                CleanupHooks.BuildServerShutdown = originalShutdown;
                Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
                DeleteDirectory(repo);
            }
        });
    }

    // These two integration controls exercise the real cleanup-to-process-protection policy.
    // Its process environment and kill hook belong to a disposable managed child, never the
    // shared test host. The same selected test runs its assertions there and returns a receipt.
    private async Task RunProcessProtectionCaseAsync(Action assertions, [CallerMemberName] string testMethod = "")
    {
        const string caseVariable = "MCG_TEST_WORKTREE_PROTECTION_CASE";
        const string receiptVariable = "MCG_TEST_WORKTREE_PROTECTION_RECEIPT";
        var childCase = Environment.GetEnvironmentVariable(caseVariable);
        var childReceipt = Environment.GetEnvironmentVariable(receiptVariable);
        if (childCase is not null || childReceipt is not null)
        {
            // A malformed child invocation must fail here, never launch another generation.
            Assert.Equal(testMethod, childCase);
            Assert.False(string.IsNullOrWhiteSpace(childReceipt));
            assertions();
            File.WriteAllText(childReceipt!, JsonSerializer.Serialize(new { TestMethod = testMethod, ProcessId = Environment.ProcessId }));
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "worktree-protection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var process = new Process();
        var started = false;
        try
        {
            var receiptPath = Path.Combine(root, "protection-receipt.json");
            var startInfo = new ProcessStartInfo
            {
                FileName = InfrastructureTestSupport.ResolveDotnetHostPath(),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(typeof(GoalWorktreeTestsRemoveCleanupProcessProtection).Assembly.Location);
            foreach (var argument in new[]
            {
                "--filter-class", typeof(GoalWorktreeTestsRemoveCleanupProcessProtection).FullName!,
                "--filter-method", typeof(GoalWorktreeTestsRemoveCleanupProcessProtection).FullName + "." + testMethod,
                "--minimum-expected-tests", "1",
                "--no-ansi", "--progress", "off"
            })
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment[caseVariable] = testMethod;
            startInfo.Environment[receiptVariable] = receiptPath;
            process.StartInfo = startInfo;
            started = process.Start();
            Assert.True(started);
            await using var child = new MtpProbeProcess(
                process, receiptPath, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await child.WaitForExitAsync(deadline.Token);
            Assert.True(result.ExitCode == 0, $"Protection child failed: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.True(File.Exists(receiptPath), "The selected child must execute the cleanup assertions before publishing its receipt.");
            using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
            Assert.Equal(testMethod, receipt.RootElement.GetProperty("TestMethod").GetString());
            // This fixture explicitly launches the managed in-process host. A different host
            // topology must fail closed rather than accepting an unowned receipt producer.
            Assert.Equal(process.Id, receipt.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.NotEqual(Environment.ProcessId, receipt.RootElement.GetProperty("ProcessId").GetInt32());
        }
        finally
        {
            // Covers failure before MtpProbeProcess takes ownership (for example a redirected
            // stream failing to initialize). The wrapper owns normal completion/cancellation.
            if (started)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch (InvalidOperationException) { } // The owning wrapper already disposed it.
            }
            DeleteDirectory(root);
        }
    }
}
