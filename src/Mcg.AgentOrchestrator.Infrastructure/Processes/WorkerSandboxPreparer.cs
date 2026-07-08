using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        WorkerSandboxPreparer.WritePreparationFiles(FailedRoot, Worktree, SandboxRoot);
        return true;
    }
}

internal sealed record WorkerSandboxPreparationResult(
    bool WorktreeRecursiveRelabel,
    bool SandboxRecursiveRelabel,
    WorkerSandboxPrepRecoverableAction? RecoveryAction = null,
    bool PrepReceiptHit = false)
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
    internal const string ReceiptFileName = ".mcg-sandbox-prep-receipt-v1.json";
    internal const string LowInheritableLevel = "(OI)(CI)L";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static WorkerSandboxPreparer CreateDefault() => new(new IcaclsIntegrityLabeler());

    public WorkerSandboxPreparationResult Prepare(string worktree, string sandboxRoot)
    {
        var reusedWorktree = Directory.Exists(worktree);
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(sandboxRoot);

        var worktreeResult = EnsureLowIntegrityRoot(worktree, sandboxRoot, allowRecursiveMigration: true, reusedWorktree);
        if (worktreeResult.RecoveryAction is not null)
        {
            return worktreeResult;
        }

        var sandboxResult = EnsureLowIntegrityRoot(sandboxRoot, sandboxRoot, allowRecursiveMigration: false, reusedWorktree);
        return sandboxResult.RecoveryAction is not null
            ? sandboxResult
            : new WorkerSandboxPreparationResult(
                worktreeResult.WorktreeRecursiveRelabel,
                sandboxResult.SandboxRecursiveRelabel,
                PrepReceiptHit: worktreeResult.PrepReceiptHit && sandboxResult.PrepReceiptHit);
    }

    private WorkerSandboxPreparationResult EnsureLowIntegrityRoot(
        string path,
        string sandboxRoot,
        bool allowRecursiveMigration,
        bool reusedWorktree)
    {
        var receiptHit = HasValidReceipt(path, sandboxRoot) && IsPrepared(path);
        if (receiptHit)
        {
            return new WorkerSandboxPreparationResult(false, false, PrepReceiptHit: true);
        }

        var recursive = allowRecursiveMigration;
        if (!labeler.SetIntegrity(path, LowInheritableLevel, recursive))
        {
            var reason = $"Failed to apply inheritable Low integrity label to '{path}'.";
            if (!reusedWorktree)
            {
                throw new InvalidOperationException(reason);
            }

            var action = new WorkerSandboxPrepRecoverableAction(
                Worktree: path == sandboxRoot ? Path.GetDirectoryName(sandboxRoot) ?? sandboxRoot : path,
                SandboxRoot: sandboxRoot,
                FailedRoot: path,
                Reason: reason,
                RequiresRecursiveRemediation: recursive);
            return new WorkerSandboxPreparationResult(false, false, action);
        }

        WritePreparationFiles(path, path == sandboxRoot ? Path.GetDirectoryName(sandboxRoot) ?? sandboxRoot : path, sandboxRoot);
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

    private static string ReceiptPath(string path) => Path.Combine(path, ReceiptFileName);

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

    internal static void WritePreparationFiles(string path, string worktree, string sandboxRoot)
    {
        WriteMarker(path);
        WriteReceipt(path, worktree, sandboxRoot);
    }

    private bool HasValidReceipt(string path, string sandboxRoot)
    {
        var receiptPath = ReceiptPath(path);
        if (!File.Exists(receiptPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(receiptPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) ||
                !version.TryGetInt32(out var versionValue) ||
                versionValue != 1 ||
                !TryGetString(root, "path", out var recordedPath) ||
                !TryGetString(root, "worktree", out var recordedWorktree) ||
                !TryGetString(root, "sandboxRoot", out var recordedSandboxRoot) ||
                !TryGetString(root, "contentHash", out var recordedHash))
            {
                return false;
            }

            var worktree = path == sandboxRoot
                ? Path.GetDirectoryName(sandboxRoot) ?? sandboxRoot
                : path;
            return PathsEqual(recordedPath, path) &&
                PathsEqual(recordedWorktree, worktree) &&
                PathsEqual(recordedSandboxRoot, sandboxRoot) &&
                string.Equals(recordedHash, ComputeReceiptContentHash(path, worktree, sandboxRoot), StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteReceipt(string path, string worktree, string sandboxRoot)
    {
        var receipt = new
        {
            version = 1,
            path = Path.GetFullPath(path),
            worktree = Path.GetFullPath(worktree),
            sandboxRoot = Path.GetFullPath(sandboxRoot),
            contentHash = ComputeReceiptContentHash(path, worktree, sandboxRoot),
            preparedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        File.WriteAllText(ReceiptPath(path), JsonSerializer.Serialize(receipt, JsonOptions) + Environment.NewLine);
    }

    private static string ComputeReceiptContentHash(string path, string worktree, string sandboxRoot)
    {
        var payload = string.Join(
            "\n",
            "v1",
            NormalizePath(path),
            NormalizePath(worktree),
            NormalizePath(sandboxRoot),
            Directory.GetCreationTimeUtc(worktree).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Directory.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
