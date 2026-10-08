using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliConductorCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("conductor", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        IConductorProcessLauncher? launcher = null, IConductorLockProbe? lockProbe = null,
        Func<DateTimeOffset>? bootTime = null, Func<string?>? mainCommit = null,
        TextWriter? output = null, TextWriter? error = null,
        Func<OrchestratorWorkspace, ITransactionalOrchestratorStateRepository>? stateRepository = null,
        OrchestratorHome? home = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        lockProbe ??= new SystemConductorLockProbe();
        if (args.Count < 2 || args.Count > 3 ||
            args.Count == 3 && !(args[1].Equals("start", StringComparison.OrdinalIgnoreCase) &&
                args[2].Equals("--clear-stop", StringComparison.OrdinalIgnoreCase)) &&
                !args[1].Equals("apply-intents", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine(CliCommandHelp.ConductorUsage);
            return 1;
        }
        try
        {
            var owner = lockProbe.ActiveOwnerPid(workspace);
            switch (args[1].ToLowerInvariant())
            {
                case "apply-intents" when args.Count == 3:
                    return ApplyIntents(args, workspace, owner, stateRepository, output, error);
                case "start":
                    return Start(workspace, owner, args.Count == 3, launcher ?? new SystemConductorProcessLauncher(),
                        output, error, home ?? OrchestratorHome.ResolveForProcess());
                case "status":
                    CliConductorStatusReader.Print(workspace, owner,
                        (bootTime ?? (() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)))(), output,
                        mainCommit ?? (() => ResolveMainCommit(workspace)));
                    return 0;
                case "stop":
                    if (owner is null)
                    {
                        output.WriteLine("No conductor is running; nothing to stop.");
                        return 0;
                    }
                    var stopFile = Path.Combine(workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
                    using (new FileStream(stopFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
                    output.WriteLine($"Stop requested for conductor pid {owner}. {stopFile}");
                    output.WriteLine("This is a detach, not a drain: live workers are detached without waiting for them to finish.");
                    return 0;
                default:
                    error.WriteLine(CliCommandHelp.ConductorUsage);
                    return 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            Win32Exception or TimeoutException or JsonException)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int ApplyIntents(IReadOnlyList<string> args, OrchestratorWorkspace workspace, int? owner,
        Func<OrchestratorWorkspace, ITransactionalOrchestratorStateRepository>? stateRepository,
        TextWriter output, TextWriter error)
    {
        if (owner is not null)
        {
            error.WriteLine($"Refused: conductor running (pid {owner}); the running conductor applies pending intents on its next tick.");
            return 1;
        }

        ProgramStartupLifecycle.EnsureStateDbInitialized(args, workspace);
        var repository = stateRepository is null
            ? new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            : stateRepository(workspace);
        var prefix = args[2];
        var matches = repository.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            error.WriteLine($"Error: Goal '{prefix}' was not found.");
            return 1;
        }
        if (matches.Length > 1)
        {
            error.WriteLine($"Error: Goal prefix '{prefix}' is ambiguous.");
            return 1;
        }

        var goalId = new GoalId(matches[0].Id);
        var attempts = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), workspace.ExecutionDirectory);
        var outcome = OperatorIntentGoalApplication.ApplyOffline(
            workspace, repository, OperatorIntentCoordinator.CreateDefault(workspace), attempts, goalId)
            .GetAwaiter().GetResult();
        switch (outcome)
        {
            case OperatorIntentOfflineOutcome.Applied applied:
                foreach (var line in applied.Lines)
                    output.WriteLine(line);
                return 0;
            case OperatorIntentOfflineOutcome.NothingPending:
                output.WriteLine($"No pending operator intents for goal {goalId.Value[..Math.Min(8, goalId.Value.Length)]}");
                return 0;
            case OperatorIntentOfflineOutcome.Refused refused:
                error.WriteLine(refused.Message);
                return 1;
            case OperatorIntentOfflineOutcome.NotDurable notDurable:
                error.WriteLine($"Not applied: {notDurable.Reason} The claimed intents stay for the next applier run or conductor tick.");
                return 1;
            default:
                throw new InvalidOperationException($"Unexpected offline operator intent outcome: {outcome.GetType().Name}.");
        }
    }

    private static string? ResolveMainCommit(OrchestratorWorkspace workspace)
    {
        try
        {
            var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{workspace.IntegrationBranch}");
            if (!result.Succeeded || result.DrainTimedOut) return null;
            var commit = result.Output.Trim();
            return commit.Length == 0 ? null : commit;
        }
        catch (Exception)
        {
            // Main lookup is optional status evidence; git failures must keep status successful.
            return null;
        }
    }

    private static int Start(OrchestratorWorkspace workspace, int? owner, bool clearStop,
        IConductorProcessLauncher launcher, TextWriter output, TextWriter error, OrchestratorHome home)
    {
        if (owner is not null)
        {
            error.WriteLine($"Refused: conductor already running (pid {owner}).");
            return 1;
        }
        var stopFile = Path.Combine(workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
        if (File.Exists(stopFile))
        {
            if (!clearStop)
            {
                error.WriteLine($"Refused: stop file exists: {stopFile}. Use --clear-stop to remove it and start.");
                return 1;
            }
            File.Delete(stopFile);
        }
        List<string> launchArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(home.RootDirectory, "scripts", "Start-OrchestratorCommand.ps1"),
                "-Name", "conduct-loop-daemon", "conduct", "--loop", "--daemon", "--watch",
                "--poll-seconds", "120", "--max-duration", "43200"];
        if (workspace.IsProjectScoped)
            launchArguments.Add($"--project={workspace.ProjectName}");
        var request = new ConductorLaunchRequest("pwsh",
            launchArguments,
            workspace.ExecutionDirectory,
            new Dictionary<string, string> { ["MCG_DISPATCH_MAX_RUNTIME_MIN"] = "120" },
            [CliProtectedProcessEnvironment.ProtectedPidVariable,
                CliProtectedProcessEnvironment.ProtectedStartTicksVariable]);
        var result = launcher.Launch(request);
        if (result.ExitCode != 0)
        {
            error.WriteLine(result.ExitCode == 2
                ? $"Refused: stop file exists: {stopFile}."
                : $"Conductor launcher failed (exit {result.ExitCode}): {result.StandardError}");
            return 1;
        }
        using var receipt = JsonDocument.Parse(result.StandardOutput ?? "");
        var pid = receipt.RootElement.GetProperty("pid").GetInt32();
        var stdoutPath = receipt.RootElement.GetProperty("stdoutPath").GetString();
        output.WriteLine($"Conductor launched: pid {pid}; stdout log: {stdoutPath}");
        output.WriteLine("Check status: mcg-orchestrator.cmd conductor status");
        return 0;
    }
}
