using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class CliExecutionContext(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    IModelProviderRegistry providers,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog workerProfiles,
    Goal? currentGoal,
    IOperatorChannel? channel = null,
    Func<AgentOrchestratorKernel>? reloadKernel = null,
    Action<AgentOrchestratorKernel>? persistKernel = null,
    Func<AcceptanceMergeCommitRequest, AcceptanceMergeCommitResult>? finalizeAcceptanceMerge = null,
    Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalKernel = null,
    Func<AcceptanceHostStopRequest, AcceptanceHostStopResult>? stopAcceptanceHosts = null,
    CliPhaseTimingRecorder? phaseTimings = null,
    Action? releaseConductLoopLease = null,
    Action? reacquireConductLoopLease = null,
    Func<AgentOrchestratorKernel>? reloadResolvedParkedHumanWaitKernel = null,
    Func<AgentOrchestratorKernel>? reloadParkedGoalSafetyNetKernel = null,
    Action<OrchestratorStateOutboxMessage>? registerStateOutboxMessage = null)
{
public AgentOrchestratorKernel Kernel { get; } = kernel;

public AgentOrchestratorKernel ReloadKernel() => reloadKernel?.Invoke() ?? Kernel;

public AgentOrchestratorKernel ReloadParkedGoalSafetyNetKernel() =>
    reloadParkedGoalSafetyNetKernel?.Invoke() ?? new AgentOrchestratorKernel();

public AgentOrchestratorKernel ReloadResolvedParkedHumanWaitKernel() =>
    reloadResolvedParkedHumanWaitKernel?.Invoke() ?? new AgentOrchestratorKernel();

/// <summary>
/// Durably commits the current kernel state mid-command. Long-running loops (conduct --loop/--watch)
/// run outside the single wrapping state transaction and call this per tick so each tick's progress
/// survives a reload or a killed process. No-op for ordinary commands (which commit on return).
/// </summary>
public void PersistCheckpoint(AgentOrchestratorKernel checkpointKernel) => persistKernel?.Invoke(checkpointKernel);

public void PersistGoalCheckpoint(AgentOrchestratorKernel checkpointKernel, IReadOnlyCollection<GoalId> changedGoalIds) =>
    persistGoalKernel?.Invoke(checkpointKernel, changedGoalIds);

public void CommitWithState(
    OrchestratorStateOutboxMessage message,
    Action applyCommittedSideEffect)
{
    if (registerStateOutboxMessage is null)
    {
        applyCommittedSideEffect();
        return;
    }

    registerStateOutboxMessage(message);
}

public OrchestratorWorkspace Workspace { get; } = workspace;

public string AgentCatalogPath => Workspace.AgentCatalogPath;

public IModelProviderRegistry Providers { get; } = providers;

public string WorkerProfilePath => Workspace.WorkerProfilePath;

public IReadOnlyList<AgentDefinition> Agents { get; set; } = agents;

public WorkerProfileCatalog WorkerProfiles { get; set; } = workerProfiles;

public Goal? CurrentGoal { get; set; } = currentGoal;

public IGoalAcceptanceVerifier AcceptanceVerifier { get; init; } = new GoalAcceptanceVerifier();

public ICliGoalWorktreeService Worktrees { get; init; } = DefaultCliGoalWorktreeService.Instance;

public IGoalLifecycleEventWriter EventWriter { get; init; } = NullGoalLifecycleEventWriter.Instance;

public Func<long>? GoalMarkLandedElapsedMilliseconds { get; init; }

public CliPhaseTimingRecorder PhaseTimings { get; } = phaseTimings ?? CliPhaseTimingRecorder.Null;

public TimeSpan? StableSlotAcquisitionTimeout { get; init; }

public Func<TimeSpan?, Action<DotnetBuildStableSlotWait>?, DotnetBuildEnvironmentLease>? StableSlotSelector { get; init; }

public TimeSpan? RunGoalPollInterval { get; init; }

public RunGoalService.SleepFunc? RunGoalSleep { get; init; }

public Func<Goal, Task<RunGoalService.RunGoalResult>>? RunGoalOverride { get; init; }

public Action ReleaseConductLoopLease { get; } = releaseConductLoopLease ?? (() => { });

public Action ReacquireConductLoopLease { get; } = reacquireConductLoopLease ?? (() => { });

public AcceptanceMergeCommitResult FinalizeAcceptanceMerge(AcceptanceMergeCommitRequest request) =>
    finalizeAcceptanceMerge?.Invoke(request) ?? request.Merge();

public AcceptanceHostStopResult StopAcceptanceHosts(AcceptanceHostStopRequest request) =>
    stopAcceptanceHosts?.Invoke(request) ?? AcceptanceHostStopper.Stop(request);

public IOperatorChannel Channel { get; } = channel ?? NullOperatorChannel.Instance;
}

internal sealed class CliPhaseTimingRecorder(string commandName, bool enabled = true)
{
    public static CliPhaseTimingRecorder Null { get; } = new("none", enabled: false);

    public void Record(string phase, TimeSpan elapsed, params (string Key, object? Value)[] context)
    {
        if (!enabled)
        {
            return;
        }

        var fields = context
            .Where(item => item.Value is not null)
            .Select(item => $"{item.Key}={FormatValue(item.Value!)}")
            .ToArray();
        var suffix = fields.Length == 0 ? string.Empty : " " + string.Join(' ', fields);
        Console.WriteLine($"PHASE_TIMING command={commandName} phase={phase} elapsedMs={Math.Max(0, (long)elapsed.TotalMilliseconds)}{suffix}");
    }

    private static string FormatValue(object value)
    {
        var text = value switch
        {
            bool flag => flag ? "true" : "false",
            TimeSpan duration => $"{Math.Max(0, (long)duration.TotalMilliseconds)}ms",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };

        return text.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? text
            : $"\"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}

internal sealed record AcceptanceMergeCommitRequest(
    GoalId GoalId,
    string ExpectedGoalFingerprint,
    string? TestedWorktreeHead,
    Func<AcceptanceMergeCommitResult> Merge,
    string CompletionReason);

internal sealed record AcceptanceMergeCommitResult(
    bool FastForwarded,
    string? Message,
    bool GuardFailure = false);

internal interface ICliGoalWorktreeService
{
    string BranchName(GoalId goalId);

    string Ensure(string executionDirectory, GoalId goalId);

    string? TryResolve(string executionDirectory, GoalId goalId);

    GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel = null,
        int? gitTimeoutMilliseconds = null);

    GoalWorktreeRemoveResult RemoveTerminalNow(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel kernel);

    bool IsGitWorkTree(string executionDirectory);

    GoalWorktreeMergeResult? TryFastForwardMerge(string executionDirectory, GoalId goalId);

    GoalWorktreeMergeResult? TryFastForwardMerge(
        string executionDirectory,
        GoalId goalId,
        Func<string?> mutationBlocker)
    {
        var blockReason = mutationBlocker();
        return !string.IsNullOrWhiteSpace(blockReason)
            ? new GoalWorktreeMergeResult(
                false,
                BranchName(goalId),
                $"Fast-forward blocked before merge: {blockReason}",
                null)
            : TryFastForwardMerge(executionDirectory, goalId);
    }

    GoalWorktreeRebaseResult TryRebaseOntoMain(string executionDirectory, GoalId goalId);

    bool NeedsRebaseOntoMain(string executionDirectory, GoalId goalId);

    bool IsWorktreeClean(string executionDirectory, GoalId goalId);

    bool HasChangesAgainstMain(string executionDirectory, GoalId goalId);

    string ResolveHead(string worktreePath);

    IReadOnlyList<string> GetChangedFiles(string worktreePath);

    GoalAcceptanceEvidenceBundle BuildAcceptanceEvidence(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string? worktreePath,
        AcceptanceVerificationResult? verification,
        bool verificationSkipped,
        string? executionDirectory = null);
}

internal sealed class DefaultCliGoalWorktreeService : ICliGoalWorktreeService
{
    public static DefaultCliGoalWorktreeService Instance { get; } = new();

    private DefaultCliGoalWorktreeService() { }

    public string BranchName(GoalId goalId) => GoalWorktrees.BranchName(goalId);

    public string Ensure(string executionDirectory, GoalId goalId) => GoalWorktrees.Ensure(executionDirectory, goalId);

    public string? TryResolve(string executionDirectory, GoalId goalId) => GoalWorktrees.TryResolve(executionDirectory, goalId);

    public GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel = null,
        int? gitTimeoutMilliseconds = null) =>
        gitTimeoutMilliseconds is { } timeout
            ? GoalWorktrees.Remove(executionDirectory, goalId, kernel, timeout)
            : GoalWorktrees.Remove(executionDirectory, goalId, kernel);

    public GoalWorktreeRemoveResult RemoveTerminalNow(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel kernel) =>
        GoalWorktrees.RemoveTerminalNow(executionDirectory, goalId, kernel);

    public bool IsGitWorkTree(string executionDirectory) => GoalWorktrees.IsGitWorkTree(executionDirectory);

    public GoalWorktreeMergeResult? TryFastForwardMerge(string executionDirectory, GoalId goalId) =>
        GoalWorktrees.TryFastForwardMerge(executionDirectory, goalId);

    public GoalWorktreeMergeResult? TryFastForwardMerge(
        string executionDirectory,
        GoalId goalId,
        Func<string?> mutationBlocker) =>
        GoalWorktrees.TryFastForwardMerge(executionDirectory, goalId, mutationBlocker);

    public GoalWorktreeRebaseResult TryRebaseOntoMain(string executionDirectory, GoalId goalId) =>
        GoalWorktrees.TryRebaseOntoMain(executionDirectory, goalId);

    public bool NeedsRebaseOntoMain(string executionDirectory, GoalId goalId) =>
        GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", "HEAD", BranchName(goalId)).ExitCode != 0;

    public bool IsWorktreeClean(string executionDirectory, GoalId goalId) => GoalWorktrees.IsWorktreeClean(executionDirectory, goalId);

    public bool HasChangesAgainstMain(string executionDirectory, GoalId goalId) =>
        GoalWorktrees.HasChangesAgainstMain(executionDirectory, goalId);

    public string ResolveHead(string worktreePath)
    {
        var result = GitCli.Run(worktreePath, "rev-parse", "HEAD");
        return result.Succeeded
            ? result.Output.Trim()
            : string.Empty;
    }

    public IReadOnlyList<string> GetChangedFiles(string worktreePath) =>
        GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);

    public GoalAcceptanceEvidenceBundle BuildAcceptanceEvidence(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string? worktreePath,
        AcceptanceVerificationResult? verification,
        bool verificationSkipped,
        string? executionDirectory = null) =>
        GoalAcceptanceEvidenceBundleBuilder.Build(kernel, goal, worktreePath, verification, verificationSkipped, executionDirectory);
}

internal sealed record AcceptanceHostStopRequest(
    GoalId GoalId,
    string? WorktreePath,
    TimeSpan Timeout);

internal sealed record AcceptanceHostProcess(
    int ProcessId,
    string ProcessName,
    string? CommandLine,
    string? LogPath);

internal sealed record AcceptanceHostStopResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<AcceptanceHostProcess> RemainingHosts)
{
    public static AcceptanceHostStopResult Success(string message) => new(true, message, []);
}

internal static class AcceptanceHostStopper
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    public static AcceptanceHostStopResult Stop(AcceptanceHostStopRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorktreePath))
        {
            return AcceptanceHostStopResult.Success("Stop-host: no goal worktree registered.");
        }

        if (TryGetCurrentGoalAppHost(request.WorktreePath, out var currentHost))
        {
            return new AcceptanceHostStopResult(
                false,
                $"BLOCKER step=stop-host reason=current-process-bound-to-worktree hosts={FormatHost(currentHost)} action=\"Run acceptance from a root-hosted CLI, or stop this exact app-host PID after this response and rerun acceptance.\"",
                [currentHost]);
        }

        var hosts = FindGoalAppHosts(request.WorktreePath).ToList();
        if (hosts.Count == 0)
        {
            return AcceptanceHostStopResult.Success("Stop-host: no goal app-host process detected.");
        }

        foreach (var host in hosts)
        {
            TryKillProcessTree(host.ProcessId);
        }

        var deadline = DateTimeOffset.UtcNow.Add(request.Timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = hosts.Where(host => IsRunning(host.ProcessId)).ToList();
            if (remaining.Count == 0)
            {
                return AcceptanceHostStopResult.Success($"Stop-host: stopped {hosts.Count} goal app-host process(es).");
            }

            Thread.Sleep(DefaultPollInterval);
        }

        var survivors = hosts.Where(host => IsRunning(host.ProcessId)).ToList();
        if (survivors.Count == 0)
        {
            return AcceptanceHostStopResult.Success($"Stop-host: stopped {hosts.Count} goal app-host process(es).");
        }

        var hostDetails = string.Join("; ", survivors.Select(FormatHost));
        return new AcceptanceHostStopResult(
            false,
            $"BLOCKER step=stop-host reason=timeout timeout={request.Timeout.TotalSeconds:0}s hosts={hostDetails} action=\"Stop exact PID(s), inspect log path(s), then rerun acceptance.\"",
            survivors);
    }

    private static IEnumerable<AcceptanceHostProcess> FindGoalAppHosts(string worktreePath)
    {
        var candidates = EnumerateCandidateProcesses();
        var commandLines = ProcessCommandLines.Read(candidates.Select(process => process.ProcessId));
        var normalizedWorktree = NormalizePath(worktreePath);
        foreach (var candidate in candidates)
        {
            commandLines.TryGetValue(candidate.ProcessId, out var commandLine);
            var haystacks = new[] { candidate.ExecutablePath, commandLine };
            if (!haystacks.Any(value => ReferencesPath(value, normalizedWorktree)))
            {
                continue;
            }

            if (!IsAppHost(candidate.ProcessName, candidate.ExecutablePath, commandLine))
            {
                continue;
            }

            yield return new AcceptanceHostProcess(
                candidate.ProcessId,
                candidate.ProcessName,
                commandLine,
                TryExtractLogPath(commandLine));
        }
    }

    private static bool TryGetCurrentGoalAppHost(string worktreePath, out AcceptanceHostProcess host)
    {
        using var current = Process.GetCurrentProcess();
        var commandLines = ProcessCommandLines.Read([Environment.ProcessId]);
        commandLines.TryGetValue(Environment.ProcessId, out var commandLine);
        var executablePath = TryGetExecutablePath(current);
        var normalizedWorktree = NormalizePath(worktreePath);
        if (IsAppHost(current.ProcessName, executablePath, commandLine) &&
            (ReferencesPath(executablePath, normalizedWorktree) || ReferencesPath(commandLine, normalizedWorktree)))
        {
            host = new AcceptanceHostProcess(
                Environment.ProcessId,
                current.ProcessName,
                commandLine,
                TryExtractLogPath(commandLine));
            return true;
        }

        host = new AcceptanceHostProcess(0, string.Empty, null, null);
        return false;
    }

    private static List<(int ProcessId, string ProcessName, string? ExecutablePath)> EnumerateCandidateProcesses()
    {
        var result = new Dictionary<int, (int ProcessId, string ProcessName, string? ExecutablePath)>();
        foreach (var processName in new[] { "Mcg.AgentOrchestrator.App", "dotnet" })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        if (process.Id == Environment.ProcessId)
                        {
                            continue;
                        }

                        result[process.Id] = (process.Id, process.ProcessName, TryGetExecutablePath(process));
                    }
                }
            }
            catch
            {
                // Best-effort process discovery; unreadable process groups are skipped.
            }
        }

        return result.Values.ToList();
    }

    private static bool IsAppHost(string processName, string? executablePath, string? commandLine)
    {
        return processName.Equals("Mcg.AgentOrchestrator.App", StringComparison.OrdinalIgnoreCase) ||
            Contains(commandLine, "Mcg.AgentOrchestrator.App") ||
            Contains(commandLine, "App.dll") ||
            Contains(executablePath, "Mcg.AgentOrchestrator.App");
    }

    private static bool ReferencesPath(string? value, string normalizedPath)
    {
        return value is not null &&
            NormalizePath(value).Contains(normalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? value, string needle) =>
        value?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    private static string NormalizePath(string value) =>
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractLogPath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var markerIndex = commandLine.IndexOf(".log", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = commandLine.LastIndexOfAny(['"', ' '], markerIndex);
        var end = commandLine.IndexOfAny(['"', ' '], markerIndex);
        start = start < 0 ? 0 : start + 1;
        end = end < 0 ? commandLine.Length : end;
        return commandLine[start..end];
    }

    private static void TryKillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The timeout check below reports survivors; exited or inaccessible processes need no action here.
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string FormatHost(AcceptanceHostProcess host)
    {
        var log = string.IsNullOrWhiteSpace(host.LogPath) ? "unknown" : host.LogPath;
        return $"pid={host.ProcessId} name={host.ProcessName} log={log}";
    }
}
