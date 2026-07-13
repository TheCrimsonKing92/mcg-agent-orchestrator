using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;

public abstract class WorkerDispatchTestSupport
{
protected static AgentDefinition TestSubscriptionAgent(string id, string name, AgentRole role) =>
    new(
        new AgentId(id),
        name,
        role,
        new ModelProfile("Test", "test-model", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("test-subscription", "test-model", "low"));



























































































































    protected static TaskBrief CreateBudgetBrief(params string[] lines)
{
    return new TaskBrief(
        new GoalId("goal123456789"),
        new TaskId("task123456789"),
        AgentRole.Developer,
        "Developer: budget",
        string.Join(Environment.NewLine, lines));
}

    protected static ProcessStartInfo CreateSandboxStartInfo(string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = WorkerShell.Executable,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.Environment.Remove("ANTHROPIC_API_KEY");
    startInfo.Environment.Remove("CLAUDE_CONFIG_DIR");
    return startInfo;
}

    protected static TaskDispatchRecord ReopenTaskWithRecoverableDispatchLimit(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task)
{
    var previousDispatch = new TaskDispatchRecord(
        "previous-worker",
        "previous command",
        "C:\\repo",
        DateTimeOffset.Parse("2026-06-01T15:00:00Z"),
        "OpenAI",
        "gpt-5.5",
        "high",
        TaskComplexity.Simple,
        123,
        WorkerProviderKind: ProviderKind.OpenAICodexCli);
    kernel.RecordTaskDispatch(goal.Id, task.Id, previousDispatch);
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
        previousDispatch.Command,
        previousDispatch.WorkingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        DateTimeOffset.Parse("2026-06-01T15:01:00Z")));
    return previousDispatch;
}

    protected static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process) CreateRecordedDispatch(
        string root,
        IClock clock,
        AgentRole role = AgentRole.Developer)
{
    Directory.CreateDirectory(root);
    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, $"{role}.out.log");
    var stderr = Path.Combine(logs, $"{role}.err.log");
    var exit = Path.Combine(logs, $"{role}.exit.txt");
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);

    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), $"{role} task.", role);
    var goal = kernel.CreateGoal("Dispatch state surface goal", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId(role.ToString().ToLowerInvariant()),
        role.ToString(),
        role,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single(candidate => candidate.RequiredRole == role);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, clock.UtcNow));
    var process = new TaskProcessRecord(111, "codex exec prompt", root, stdout, stderr, exit, clock.UtcNow, null, null, OwnedProcessIds: [111]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    return (kernel, goal, task, process);
}

    protected static DispatchStateSurface CreateStateSurface(
        IClock clock,
        IReadOnlyCollection<int> livePids,
        IReadOnlyDictionary<int, string> commandLines) =>
        new(
            clock,
            isProcessAlive: livePids.Contains,
            readCommandLines: pids => pids
                .Distinct()
                .Where(commandLines.ContainsKey)
                .ToDictionary(pid => pid, pid => commandLines[pid]));

    protected static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process) CreateCompletedGoalWorktreeDispatch(
        string root,
        AgentRole role,
        string standardOutput,
        string standardError,
        IClock clock,
        Action<string>? mutateWorktree = null,
        string? taskDescription = null,
        string? verificationPlan = null,
        bool sandboxLowIntegrity = false,
        string workerName = "codex-cli",
        string command = "codex exec prompt",
        ProviderKind workerProviderKind = ProviderKind.Unknown,
        string? providerName = null)
{
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), taskDescription ?? $"{role} task.", role, verificationPlan);
    var goal = kernel.CreateGoal("Dispatch evidence goal", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId(role.ToString().ToLowerInvariant()),
        role.ToString(),
        role,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);

    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    mutateWorktree?.Invoke(worktree);
    var head = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);
    standardOutput = standardOutput.Replace("{commit}", head, StringComparison.Ordinal);
    standardError = standardError.Replace("{commit}", head, StringComparison.Ordinal);

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, $"{role}.out.log");
    var stderr = Path.Combine(logs, $"{role}.err.log");
    var exit = Path.Combine(logs, $"{role}.exit.txt");
    File.WriteAllText(stdout, standardOutput);
    File.WriteAllText(stderr, standardError);
    File.WriteAllText(exit, "0");

    var task = goal.Tasks.Single(candidate => candidate.RequiredRole == role);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        workerName,
        command,
        worktree,
        clock.UtcNow,
        providerName,
        SandboxLowIntegrity: sandboxLowIntegrity,
        WorkerProviderKind: workerProviderKind));
    var process = new TaskProcessRecord(999999, command, worktree, stdout, stderr, exit, clock.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    return (kernel, goal, task, process);
}

    protected static void WriteHeartbeat(
    TaskProcessRecord process,
    DateTimeOffset lastObservedAt,
    DateTimeOffset lastProgressAt,
    string state,
    long stdoutBytes,
    long stderrBytes,
    int? childPid = 888888,
    long? ownedCpuMs = null,
    IReadOnlyList<int>? ownedPids = null,
    bool exitFileExists = false)
{
    var childPidJson = childPid.HasValue ? childPid.Value.ToString() : "null";
    var ownedCpuMsJson = ownedCpuMs.HasValue ? $",\"ownedCpuMs\":{ownedCpuMs.Value}" : string.Empty;
    var ownedPidsJson = ownedPids is { Count: > 0 }
        ? string.Join(",", ownedPids)
        : string.Empty;
    var exitFileExistsJson = exitFileExists ? "true" : "false";
    File.WriteAllText(
        BackgroundDispatchRunner.GetHeartbeatPath(process),
        "{" +
        "\"pid\":999999," +
        $"\"childPid\":{childPidJson}," +
        $"\"ownedPids\":[{ownedPidsJson}]," +
        $"\"startedAt\":\"{process.StartedAt:O}\"," +
        $"\"lastObservedAt\":\"{lastObservedAt:O}\"," +
        $"\"lastProgressAt\":\"{lastProgressAt:O}\"," +
        $"\"state\":\"{state}\"," +
        $"\"stdoutBytes\":{stdoutBytes}," +
        $"\"stderrBytes\":{stderrBytes}," +
        $"\"exitFileExists\":{exitFileExistsJson}" +
        ownedCpuMsJson +
        "}");
}

    // Clears the operator's MCG_WORKER_SANDBOX for the duration of a test so dispatch-mode assertions
    // are hermetic — WorkerProfileDispatcher reads WorkerSandboxOptions.FromEnvironment(), so a test
    // asserting the default (workspace-write) sandbox mode would otherwise fail when the suite is run
    // under `conduct`/acceptance with MCG_WORKER_SANDBOX=1 set. Restores the prior value on dispose.
    protected static WorkerSandboxEnvRestore ClearWorkerSandboxEnv()
    {
        var previous = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
        Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, null);
        return new WorkerSandboxEnvRestore(previous);
    }

    protected sealed class WorkerSandboxEnvRestore(string? previous) : IDisposable
    {
        public void Dispose() =>
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previous);
    }

    protected static AgentDefinition SubscriptionPlannerAgent(string id, string name) => new(
        new AgentId(id),
        name,
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));

    protected static AgentDefinition SubscriptionDeveloperAgent() => new(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));

    protected static bool RealClaudeLauncherExists(string executable) =>
        executable.Equals("claude", StringComparison.OrdinalIgnoreCase);

    protected static WorkerProfileCatalog DispatchTestProfiles() => new(
    [
        new WorkerProfile(
            "codex-cli",
            "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"),
        new WorkerProfile(
            "claude-cli",
            "claude --model {subscriptionModelName} --permission-mode {permissionMode}")
    ]);

    protected static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        });
    }

    protected static string CreateSeededDispatchRepository()
{
    var root = CreateTempDirectory();
    RunGit(root, ["init"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    RunGit(root, ["config", "user.email", "tests@example.com"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    RunGit(root, ["config", "user.name", "Dispatch Tests"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
    RunGit(root, ["add", "-A"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    RunGit(root, ["commit", "-m", "Seed"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    return root;
}

    protected static void RunGit(string workingDirectory, string[] arguments, DateTimeOffset commitTime)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };
    startInfo.Environment["GIT_AUTHOR_DATE"] = commitTime.ToString("O");
    startInfo.Environment["GIT_COMMITTER_DATE"] = commitTime.ToString("O");

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start git.");
    process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(60000);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }
}

protected static string ReadGit(string workingDirectory, string[] arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start git.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(60000);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    return output.Trim();
}

protected static (int ExitCode, string StandardOutput, string StandardError) RunPowerShellCommand(string workingDirectory, string command)
{
    // Resolve the PowerShell host the same way production dispatch does (pwsh-preferred, with the
    // Windows-only -ExecutionPolicy), so this smoke runs natively on Linux instead of resolving
    // powershell.exe via WSL interop against a Linux working directory.
    var startInfo = new ProcessStartInfo
    {
        FileName = WorkerShell.Executable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };
    foreach (var argument in WorkerShell.BaseArguments())
    {
        startInfo.ArgumentList.Add(argument);
    }

    startInfo.ArgumentList.Add(command);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start PowerShell.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(60000);

    return (process.ExitCode, output, error);
}

protected static void WriteDispatchArtifact(string logsRoot, string prefix, string suffix, string content, DateTime lastWriteTime)
{
    var path = Path.Combine(logsRoot, prefix + suffix);
    File.WriteAllText(path, content);
    File.SetLastWriteTime(path, lastWriteTime);
}

protected static string FindRepositoryFile(params string[] relativeSegments)
{
    var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
    var candidate = Path.Combine(new[] { repositoryRoot }.Concat(relativeSegments).ToArray());
    if (File.Exists(candidate))
    {
        return candidate;
    }

    throw new FileNotFoundException($"Could not find repository file '{Path.Combine(relativeSegments)}'.");
}

protected static string WorkerResultBlock(
    string files,
    string commands,
    string tests,
    string commit = "{commit}",
    string blockers = "none",
    string modelFit = "OpenAI/gpt-5.5 - adequate - test worker fixture.",
    string skills = "dotnet-windows-build-hygiene",
    string confidence = "high")
{
    return $"""
        WORKER_RESULT:
        files: {files}
        commands: {commands}
        tests: {tests}
        commit: {commit}
        blockers: {blockers}
        model_fit: {modelFit}
        skills: {skills}
        confidence: {confidence}
        END_WORKER_RESULT
        """;
}

protected static string SandboxPrepCompleteEvent()
{
    return """
        {"event":"sandbox-prep","phase":"complete","timestamp":"2026-06-02T12:00:01.0000000Z","startedAt":"2026-06-02T12:00:00.0000000Z","workingDirectory":"test","elapsedMs":1}
        """;
}

protected static void WriteSkill(string workingDirectory, string skillName)
{
    var directory = Path.Combine(workingDirectory, ".agents", "skills", skillName);
    Directory.CreateDirectory(directory);
    File.WriteAllText(
        Path.Combine(directory, "SKILL.md"),
        $"""
        ---
        name: {skillName}
        description: Test skill fixture.
        ---

        # {skillName}
        """);
}

    protected static void WaitForExitFile(string path)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
    {
        Thread.Sleep(50);
    }

    if (!File.Exists(path))
    {
        throw new TimeoutException($"Timed out waiting for exit file '{path}'.");
    }
}

    protected static void WaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow.Add(timeout);
    while (!condition() && DateTimeOffset.UtcNow < deadline)
    {
        Thread.Sleep(50);
    }

    if (!condition())
    {
        throw new TimeoutException("Timed out waiting for test condition.");
    }
}

    protected sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    protected sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        private DateTimeOffset _utcNow = utcNow;

        public DateTimeOffset UtcNow => _utcNow;

        public void Advance() => _utcNow = _utcNow.AddSeconds(1);
    }

    protected sealed class CaptureDiagnosticWriter : IDispatchDiagnosticWriter
    {
        public List<DispatchDiagnosticRecord> Records { get; } = [];
        public void WriteRecord(DispatchDiagnosticRecord record) => Records.Add(record);
    }

    private protected sealed class WorkerDispatchRecordingIntegrityLabeler(IntegrityLabelState queryState, bool setResult = true) : IWorkerIntegrityLabeler
    {
        public List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public IntegrityLabelState Query(string path) => queryState;

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls.Add((path, level, recursive));
            return setResult;
        }
    }

    protected sealed class ThrowingDiagnosticWriter : IDispatchDiagnosticWriter
    {
        public void WriteRecord(DispatchDiagnosticRecord record) =>
            throw new InvalidOperationException("Diagnostic writer failure (test-injected).");
    }
}
