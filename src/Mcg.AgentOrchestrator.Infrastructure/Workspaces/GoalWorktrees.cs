using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalWorktreeMergeResult(
    bool FastForwarded,
    string BranchName,
    string Message,
    string? SuggestedCommand,
    IReadOnlyList<string>? ChangedFiles = null);

public sealed record GoalWorktreeChangedFilesResult(
    bool Succeeded,
    IReadOnlyList<string> Files,
    string? FailureReason);

public enum GoalWorktreeRebaseStatus
{
    Rebased,
    AlreadyFastForwardable,
    MissingBranch,
    MissingWorktree,
    DirtyWorktree,
    Conflict,
    Failed
}

public sealed record GoalWorktreeRebaseResult(
    GoalWorktreeRebaseStatus Status,
    string BranchName,
    string Message,
    IReadOnlyList<string> ConflictFiles,
    string? SuggestedCommand)
{
    public bool UpdatedBranch => Status == GoalWorktreeRebaseStatus.Rebased ||
        Status == GoalWorktreeRebaseStatus.AlreadyFastForwardable;
}

public sealed record WorktreeLockHolder(int ProcessId, string ProcessName, string? CommandLine);

public sealed record GoalWorktreeCleanupBackoff(
    string Reason,
    DateTimeOffset SkipUntilUtc,
    TimeSpan RemainingWait)
{
    public bool IsBudgetExhausted =>
        Reason.Equals("remove:cleanup-budget-exhausted", StringComparison.OrdinalIgnoreCase);
}

public sealed record GoalWorktreeCleanupDebt(
    string Path,
    string Reason,
    string LastOperation,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset SkipUntilUtc,
    TimeSpan Age,
    TimeSpan RemainingWait,
    int ConsecutiveFailureCount,
    DateTimeOffset? EscalatedAtUtc);

public sealed record GoalWorktreeRemoveResult(
    string Message,
    string? LeftoverPath,
    IReadOnlyList<WorktreeLockHolder> LockHolders,
    string? ResumeCommand,
    GoalOwnedEphemeralSweepResult? OwnedEphemeralCleanup = null,
    GoalWorktreeCleanupBackoff? CleanupBackoff = null)
{
    public bool IsComplete => LeftoverPath is null;
}

public sealed record GoalWorktreeCleanupWarning(string Path, string Operation, Exception Exception);

internal enum GoalWorktreeDeleteFailureKind
{
    None,
    AccessDenied,
    Transient,
    Unknown
}

internal sealed record GoalWorktreeDeleteResult(
    bool Succeeded,
    GoalWorktreeDeleteFailureKind FailureKind,
    string? Message)
{
    public static GoalWorktreeDeleteResult Success { get; } =
        new(true, GoalWorktreeDeleteFailureKind.None, null);

    public static GoalWorktreeDeleteResult Failed(GoalWorktreeDeleteFailureKind failureKind, string? message) =>
        new(false, failureKind, message);
}

/// <summary>
/// Immutable cleanup behavior supplied to a single worktree-cleanup operation.
/// </summary>
public sealed record GoalWorktreeCleanupHooks
{
    /// <summary>
    /// A live compatibility view over the legacy process-wide hooks. Each delegate dereferences
    /// the corresponding static when it is invoked; explicitly constructed hook records remain fixed.
    /// </summary>
    public static GoalWorktreeCleanupHooks Default { get; } = new()
    {
        BuildServerShutdown = static (path, timeout) => GoalWorktrees.BuildServerShutdown(path, timeout),
        ResetSandboxAcl = static (path, timeout) => GoalWorktrees.SandboxAclHelper.ResetSandboxAcl(path, timeout),
        TryKillRecordedProcess = static processId => GoalWorktrees.TryKillRecordedProcess(processId),
        DeleteDirectory = static path => GoalWorktrees.DeleteDirectory(path),
        DeleteDirectoryForCleanup = static path => GoalWorktrees.DeleteDirectoryForCleanup(path),
        RunWorktreeRemove = static (directory, timeout, path, force) =>
            GoalWorktrees.RunWorktreeRemove(directory, timeout, path, force),
        RunWorktreePrune = static (directory, timeout, expireNow) =>
            GoalWorktrees.RunWorktreePrune(directory, timeout, expireNow),
        FindLockHoldersForCleanup = static path => GoalWorktrees.FindLockHoldersForCleanup(path),
        CleanupWarningSink = static warning => GoalWorktrees.CleanupWarningSink(warning),
        CleanupElapsedMilliseconds = static () => GoalWorktrees.CleanupElapsedMilliseconds,
        CleanupUtcNow = static () => GoalWorktrees.CleanupUtcNow(),
        CleanupBackoffDuration = static () => GoalWorktrees.CleanupBackoffDuration,
        CleanupBudgetExhaustedBackoffDuration = static () => GoalWorktrees.CleanupBudgetExhaustedBackoffDuration,
        CleanupOptions = static () => GoalWorktrees.CleanupOptions,
        CleanupAttentionStoreDirectory = static () => GoalWorktrees.CleanupAttentionStoreDirectory
    };

    public Action<string, int> BuildServerShutdown { get; init; } = GoalWorktrees.DefaultBuildServerShutdown;
    public Action<string, int> ResetSandboxAcl { get; init; } = static (path, timeout) =>
        (OperatingSystem.IsWindows()
            ? (ISandboxAclHelper)new WindowsSandboxAclHelper()
            : new NoOpSandboxAclHelper())
            .ResetSandboxAcl(path, timeout);
    public Func<int, bool> TryKillRecordedProcess { get; init; } = GoalWorktrees.DefaultTryKillRecordedProcess;
    public Func<string, bool> DeleteDirectory { get; init; } = GoalWorktrees.DeleteDirectoryWithRetry;
    internal Func<string, GoalWorktreeDeleteResult> DeleteDirectoryForCleanup { get; init; } =
        GoalWorktrees.DeleteDirectoryWithReason;
    internal Func<string, int, string, bool, GitCli.GitResult> RunWorktreeRemove { get; init; } =
        GoalWorktrees.DefaultRunWorktreeRemove;
    internal Func<string, int, bool, GitCli.GitResult> RunWorktreePrune { get; init; } =
        GoalWorktrees.DefaultRunWorktreePrune;
    public Func<string, IReadOnlyList<WorktreeLockHolder>> FindLockHoldersForCleanup { get; init; } =
        GoalWorktrees.FindLockHolders;
    public Action<GoalWorktreeCleanupWarning> CleanupWarningSink { get; init; } =
        GoalWorktrees.DefaultCleanupWarningSink;
    public Func<Func<long>?> CleanupElapsedMilliseconds { get; init; } = static () => null;
    public Func<DateTimeOffset> CleanupUtcNow { get; init; } = static () => DateTimeOffset.UtcNow;
    public Func<TimeSpan> CleanupBackoffDuration { get; init; } = static () => TimeSpan.FromMinutes(30);
    public Func<TimeSpan> CleanupBudgetExhaustedBackoffDuration { get; init; } = static () => TimeSpan.FromMinutes(1);
    public Func<GoalWorktreeCleanupOptions> CleanupOptions { get; init; } = static () => GoalWorktreeCleanupOptions.Default;
    public Func<string?> CleanupAttentionStoreDirectory { get; init; } = static () => null;
}

public sealed record GoalWorktreeSweepResult(int RemovedCount, IReadOnlyList<string> LeftoverPaths);

public sealed record GoalOwnedEphemeralSweepResult(int RemovedCount, IReadOnlyList<string> LeftoverPaths)
{
    public bool IsComplete => LeftoverPaths.Count == 0;
}

public sealed record GoalWorktreeCleanupOptions(
    TimeSpan SweepInterval,
    int EscalationThreshold,
    TimeSpan EscalatedRetryInterval)
{
    public static GoalWorktreeCleanupOptions Default { get; } =
        new(TimeSpan.FromMinutes(5), 3, TimeSpan.FromDays(1));

    public GoalWorktreeCleanupOptions Validate()
    {
        if (SweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SweepInterval), "Sweep interval must be positive.");
        }

        if (EscalationThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(EscalationThreshold), "Escalation threshold must be at least one.");
        }

        if (EscalatedRetryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(EscalatedRetryInterval), "Escalated retry interval must be positive.");
        }

        return this;
    }
}

public sealed record GoalWorktreeGitMetadataAccess(
    string WorktreePath,
    string IndexLockPath,
    string CurrentIdentity,
    bool CurrentProcessCanWriteIndexLock,
    bool WorkerCanWriteIndexLock,
    string WorkerWriteDisposition,
    string CommitContract,
    string? Error);

public interface ISandboxAclHelper
{
    void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds);
}

public sealed class WindowsSandboxAclHelper : ISandboxAclHelper
{
    public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sandboxPath = Path.Combine(worktreePath, ".mcg-sandbox");
        if (!Directory.Exists(sandboxPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "icacls",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(sandboxPath);
        startInfo.ArgumentList.Add("/reset");
        startInfo.ArgumentList.Add("/T");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("/Q");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }
}

public sealed class NoOpSandboxAclHelper : ISandboxAclHelper
{
    public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds) { }
}

public static partial class GoalWorktrees
{
    public const string DirectoryName = ".orchestrator-worktrees";
    private static readonly TimeSpan InitialDeleteRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int DeleteRetryAttempts = 6;
    private static readonly string[] LockHolderCandidates =
        ["dotnet", "VBCSCompiler", "MSBuild", "claude", "codex", "node", "powershell", "pwsh"];
    private static readonly TimeSpan BuildServerShutdownTimeout = TimeSpan.FromSeconds(10);
    // Process-wide test seams; tests replacing these hooks must use the
    // GoalWorktreeCleanupHooks collection so replacements cannot overlap.
    // Called best-effort before directory deletion to release any
    // VBCSCompiler/Roslyn/MSBuild file handles held by the acceptance build server.
    internal static Action<string, int> BuildServerShutdown = DefaultBuildServerShutdown;
    internal static ISandboxAclHelper SandboxAclHelper { get; set; } =
        OperatingSystem.IsWindows() ? new WindowsSandboxAclHelper() : new NoOpSandboxAclHelper();
    internal static Func<int, bool> TryKillRecordedProcess { get; set; } = DefaultTryKillRecordedProcess;
    internal static Func<string, bool> DeleteDirectory { get; set; } = DeleteDirectoryWithRetry;
    internal static Func<string, GoalWorktreeDeleteResult> DeleteDirectoryForCleanup { get; set; } = DeleteDirectoryWithReason;
    internal static Func<string, int, string, bool, GitCli.GitResult> RunWorktreeRemove { get; set; } =
        DefaultRunWorktreeRemove;
    internal static Func<string, int, bool, GitCli.GitResult> RunWorktreePrune { get; set; } =
        DefaultRunWorktreePrune;
    internal static Func<string, IReadOnlyList<WorktreeLockHolder>> FindLockHoldersForCleanup { get; set; } = FindLockHolders;
    internal static Func<IEnumerable<string>, ProcessCommandLineSnapshot>? ProcessCommandLineSnapshotForCleanupTests { get; set; }
    internal static Action<GoalWorktreeCleanupWarning> CleanupWarningSink { get; set; } = DefaultCleanupWarningSink;
    internal static Func<long>? CleanupElapsedMilliseconds { get; set; }
    internal static Func<DateTimeOffset> CleanupUtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    internal static TimeSpan CleanupBackoffDuration { get; set; } = TimeSpan.FromMinutes(30);
    internal static TimeSpan CleanupBudgetExhaustedBackoffDuration { get; set; } = TimeSpan.FromMinutes(1);
    public static GoalWorktreeCleanupOptions CleanupOptions { get; private set; } = GoalWorktreeCleanupOptions.Default;
    internal static string? CleanupAttentionStoreDirectory { get; private set; }

    public static void ConfigureCleanup(
        GoalWorktreeCleanupOptions options,
        string? attentionStoreDirectory = null)
    {
        CleanupOptions = options.Validate();
        CleanupAttentionStoreDirectory = string.IsNullOrWhiteSpace(attentionStoreDirectory)
            ? null
            : Path.GetFullPath(attentionStoreDirectory);
    }

}
