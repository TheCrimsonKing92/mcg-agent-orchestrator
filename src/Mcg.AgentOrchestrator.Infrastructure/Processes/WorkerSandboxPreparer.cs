using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerSandboxPrepRecoverableAction(
    string Worktree,
    string SandboxRoot,
    string FailedRoot,
    string Reason,
    bool RequiresRecursiveRemediation)
{
    public bool Execute()
    {
        var labeler = new IcaclsIntegrityLabeler();
        if (!labeler.SetIntegrity(FailedRoot, WorkerSandboxPreparer.LowInheritableLevel, RequiresRecursiveRemediation))
        {
            return false;
        }

        WorkerSandboxPreparer.WriteMarker(FailedRoot);
        return true;
    }
}

internal sealed record WorkerSandboxPreparationResult(
    bool WorktreeRecursiveRelabel,
    bool SandboxRecursiveRelabel,
    WorkerSandboxPrepRecoverableAction? RecoveryAction = null)
{
    public bool RequiresRecovery => RecoveryAction is not null;
}

internal interface IWorkerIntegrityLabeler
{
    IntegrityLabelState Query(string path);

    bool SetIntegrity(string path, string level, bool recursive);
}

internal sealed record IntegrityLabelState(bool Exists, bool Low, bool Inheritable);

internal sealed class WorkerSandboxPreparer(IWorkerIntegrityLabeler labeler)
{
    internal const string MarkerFileName = ".mcg-low-integrity-v1";
    internal const string LowInheritableLevel = "(OI)(CI)L";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static WorkerSandboxPreparer CreateDefault() => new(new IcaclsIntegrityLabeler());

    public WorkerSandboxPreparationResult Prepare(string worktree, string sandboxRoot)
    {
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(sandboxRoot);

        var worktreeResult = EnsureLowIntegrityRoot(worktree, sandboxRoot, allowRecursiveMigration: true);
        if (worktreeResult.RecoveryAction is not null)
        {
            return worktreeResult;
        }

        var sandboxResult = EnsureLowIntegrityRoot(sandboxRoot, sandboxRoot, allowRecursiveMigration: false);
        return sandboxResult.RecoveryAction is not null
            ? sandboxResult
            : new WorkerSandboxPreparationResult(worktreeResult.WorktreeRecursiveRelabel, sandboxResult.SandboxRecursiveRelabel);
    }

    private WorkerSandboxPreparationResult EnsureLowIntegrityRoot(string path, string sandboxRoot, bool allowRecursiveMigration)
    {
        if (IsPrepared(path))
        {
            return new WorkerSandboxPreparationResult(false, false);
        }

        var recursive = allowRecursiveMigration;
        if (!labeler.SetIntegrity(path, LowInheritableLevel, recursive))
        {
            var action = new WorkerSandboxPrepRecoverableAction(
                Worktree: path == sandboxRoot ? Path.GetDirectoryName(sandboxRoot) ?? sandboxRoot : path,
                SandboxRoot: sandboxRoot,
                FailedRoot: path,
                Reason: $"Failed to apply inheritable Low integrity label to '{path}'.",
                RequiresRecursiveRemediation: recursive);
            return new WorkerSandboxPreparationResult(false, false, action);
        }

        WriteMarker(path);
        return path == sandboxRoot
            ? new WorkerSandboxPreparationResult(false, recursive)
            : new WorkerSandboxPreparationResult(recursive, false);
    }

    private bool IsPrepared(string path)
    {
        if (!File.Exists(MarkerPath(path)))
        {
            return false;
        }

        var state = labeler.Query(path);
        return state.Exists && state.Low && state.Inheritable;
    }

    private static string MarkerPath(string path) => Path.Combine(path, MarkerFileName);

    internal static void WriteMarker(string path)
    {
        var marker = new
        {
            version = 1,
            integrity = "low",
            inheritable = true,
            preparedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        File.WriteAllText(MarkerPath(path), JsonSerializer.Serialize(marker, JsonOptions) + Environment.NewLine);
    }
}

internal sealed class IcaclsIntegrityLabeler : IWorkerIntegrityLabeler
{
    private readonly Func<ProcessStartInfo, Process?> startProcess;

    public IcaclsIntegrityLabeler()
        : this(Process.Start)
    {
    }

    internal IcaclsIntegrityLabeler(Func<ProcessStartInfo, Process?> startProcess)
    {
        this.startProcess = startProcess;
    }

    public IntegrityLabelState Query(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return new IntegrityLabelState(Exists: false, Low: false, Inheritable: false);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "icacls",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(path);

            using var process = startProcess(startInfo);
            if (process is null)
            {
                return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
            }

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            {
                return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
            }

            var low = output.Contains("Low Mandatory Level", StringComparison.OrdinalIgnoreCase) ||
                output.Contains(":(OI)(CI)(NW)", StringComparison.OrdinalIgnoreCase);
            var inheritable = output.Contains("(OI)", StringComparison.OrdinalIgnoreCase) &&
                output.Contains("(CI)", StringComparison.OrdinalIgnoreCase);
            return new IntegrityLabelState(Exists: true, low, inheritable);
        }
        catch
        {
            return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        }
    }

    public bool SetIntegrity(string path, string level, bool recursive)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "icacls",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/setintegritylevel");
            psi.ArgumentList.Add(level);
            if (recursive)
            {
                psi.ArgumentList.Add("/T");
            }

            using var process = startProcess(psi);
            if (process is null)
            {
                return false;
            }

            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var copyErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var completed = DispatchProcessHost.WaitForIntegrityLabeler(process, TimeSpan.FromMinutes(2));
            try { Task.WaitAll([copyOut, copyErr], 2000); } catch { }
            return completed && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
