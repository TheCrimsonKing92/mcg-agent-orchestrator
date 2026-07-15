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
    bool PrepReceiptHit = false,
    string[]? ReceiptSkippedProtectionPhases = null)
{
    public bool RequiresRecovery => RecoveryAction is not null;

    public bool ReceiptCoversProtectionPhase(string phase) =>
        ReceiptSkippedProtectionPhases is not null &&
        ReceiptSkippedProtectionPhases.Contains(phase, StringComparer.Ordinal);
}

internal interface IWorkerIntegrityLabeler
{
    IntegrityLabelState Query(string path);

    bool SetIntegrity(string path, string level, bool recursive);
}

internal sealed record IntegrityLabelState(bool Exists, bool Low, bool Inheritable, bool Medium = false);

internal sealed class WorkerSandboxPreparer(IWorkerIntegrityLabeler labeler)
{
    internal const string MarkerFileName = ".mcg-low-integrity-v1";
    internal const string ReceiptFileName = ".mcg-sandbox-prep-receipt-v1.json";
    private const int ReceiptSchemaVersion = 2;
    internal const string LowInheritableLevel = "(OI)(CI)L";
    internal const string ProtectWorkspaceBoundaryPhase = "protect-workspace-boundary";
    internal const string ProtectGitMetadataPhase = "protect-git-metadata";
    private static readonly string[] DefaultSkippedProtectionPhases =
    [
        ProtectWorkspaceBoundaryPhase,
        ProtectGitMetadataPhase
    ];
    private static readonly string[] ReceiptVerificationBasis =
    [
        "receipt-schema-v2",
        "path-worktree-sandboxRoot-contentHash",
        "directory-creation-time",
        "low-integrity-marker",
        "low-inheritable-label"
    ];
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
                PrepReceiptHit: worktreeResult.PrepReceiptHit && sandboxResult.PrepReceiptHit,
                ReceiptSkippedProtectionPhases: IntersectProtectionPhases(
                    worktreeResult.ReceiptSkippedProtectionPhases,
                    sandboxResult.ReceiptSkippedProtectionPhases));
    }

    public WorkerSandboxPreparationResult PrepareSandboxRootOnly(string worktree, string sandboxRoot)
    {
        Directory.CreateDirectory(worktree);
        var reusedSandboxRoot = Directory.Exists(sandboxRoot);
        Directory.CreateDirectory(sandboxRoot);

        // Read-only workers run at Low integrity too, but the worktree deliberately stays Medium:
        // MIC then permits reads while denying writes. Only provider scratch needs a Low label.
        return EnsureLowIntegrityRoot(
            sandboxRoot,
            sandboxRoot,
            allowRecursiveMigration: false,
            reusedWorktree: reusedSandboxRoot);
    }

    private WorkerSandboxPreparationResult EnsureLowIntegrityRoot(
        string path,
        string sandboxRoot,
        bool allowRecursiveMigration,
        bool reusedWorktree)
    {
        var receipt = TryReadValidReceipt(path, sandboxRoot);
        if (receipt.Valid && IsPrepared(path))
        {
            var skippedProtectionPhases = path == sandboxRoot
                ? receipt.SkippedProtectionPhases
                : FilterCurrentlyCoveredProtectionPhases(path, receipt.SkippedProtectionPhases);
            return new WorkerSandboxPreparationResult(
                false,
                false,
                PrepReceiptHit: skippedProtectionPhases.Length == receipt.SkippedProtectionPhases.Length,
                ReceiptSkippedProtectionPhases: skippedProtectionPhases);
        }

        var recursive = allowRecursiveMigration;
        if (!labeler.SetIntegrity(path, LowInheritableLevel, recursive))
        {
            if (IsAlreadyLowInheritable(path))
            {
                WritePreparationFiles(path, path == sandboxRoot ? Path.GetDirectoryName(sandboxRoot) ?? sandboxRoot : path, sandboxRoot);
                return new WorkerSandboxPreparationResult(false, false);
            }

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

    private bool IsAlreadyLowInheritable(string path)
    {
        var state = labeler.Query(path);
        return state.Exists && state.Low && state.Inheritable;
    }

    private string[] FilterCurrentlyCoveredProtectionPhases(string worktree, string[] skippedProtectionPhases)
    {
        return skippedProtectionPhases
            .Where(phase => ProtectionPhaseStillCovered(worktree, phase))
            .ToArray();
    }

    private bool ProtectionPhaseStillCovered(string worktree, string phase)
    {
        return phase switch
        {
            ProtectWorkspaceBoundaryPhase => WorkspaceBoundaryStillMedium(worktree),
            ProtectGitMetadataPhase => GitMetadataStillMedium(worktree),
            _ => false
        };
    }

    private bool WorkspaceBoundaryStillMedium(string worktree)
    {
        var parent = Directory.GetParent(worktree);
        return parent is null || !parent.Exists || IsMedium(parent.FullName);
    }

    private bool GitMetadataStillMedium(string worktree)
    {
        var checkoutGitFile = Path.Combine(worktree, ".git");
        if ((File.Exists(checkoutGitFile) || Directory.Exists(checkoutGitFile)) && !IsMedium(checkoutGitFile))
        {
            return false;
        }

        var commonDir = TryResolveGitCommonDir(worktree);
        return commonDir is null || !Directory.Exists(commonDir) || IsMedium(commonDir);
    }

    private static string? TryResolveGitCommonDir(string worktree)
    {
        try
        {
            var commonDirResult = GitCli.Run(worktree, "rev-parse", "--git-common-dir");
            if (!commonDirResult.Succeeded || string.IsNullOrWhiteSpace(commonDirResult.Output))
            {
                return null;
            }

            var commonDirRaw = commonDirResult.Output.Trim();
            return Path.IsPathRooted(commonDirRaw)
                ? commonDirRaw
                : Path.GetFullPath(Path.Combine(worktree, commonDirRaw));
        }
        catch
        {
            return null;
        }
    }

    private bool IsMedium(string path)
    {
        var state = labeler.Query(path);
        return state.Exists && state.Medium;
    }

    private bool IsPrepared(string path)
    {
        if (!File.Exists(MarkerPath(path)))
        {
            return false;
        }

        return IsAlreadyLowInheritable(path);
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
        => WritePreparationFiles(path, worktree, sandboxRoot, []);

    internal static void WriteCompletedProtectionReceipts(string worktree, string sandboxRoot)
    {
        WritePreparationFiles(worktree, worktree, sandboxRoot, DefaultSkippedProtectionPhases);
        WritePreparationFiles(sandboxRoot, worktree, sandboxRoot, DefaultSkippedProtectionPhases);
    }

    internal static void WritePreparationFiles(
        string path,
        string worktree,
        string sandboxRoot,
        IReadOnlyCollection<string> skippedProtectionPhases)
    {
        WriteMarker(path);
        WriteReceipt(path, worktree, sandboxRoot, skippedProtectionPhases);
    }

    private static ReceiptValidation TryReadValidReceipt(string path, string sandboxRoot)
    {
        var receiptPath = ReceiptPath(path);
        if (!File.Exists(receiptPath))
        {
            return ReceiptValidation.Invalid;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(receiptPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) ||
                !version.TryGetInt32(out var versionValue) ||
                versionValue != ReceiptSchemaVersion ||
                !TryGetString(root, "path", out var recordedPath) ||
                !TryGetString(root, "worktree", out var recordedWorktree) ||
                !TryGetString(root, "sandboxRoot", out var recordedSandboxRoot) ||
                !TryGetString(root, "contentHash", out var recordedHash) ||
                !TryGetStringArray(root, "skippedProtectionPhases", out var skippedProtectionPhases) ||
                !TryGetStringArray(root, "verificationBasis", out var verificationBasis))
            {
                return ReceiptValidation.Invalid;
            }

            var worktree = path == sandboxRoot
                ? Path.GetDirectoryName(sandboxRoot) ?? sandboxRoot
                : path;
            if (!PathsEqual(recordedPath, path) ||
                !PathsEqual(recordedWorktree, worktree) ||
                !PathsEqual(recordedSandboxRoot, sandboxRoot) ||
                !HasRequiredVerificationBasis(verificationBasis) ||
                !string.Equals(
                    recordedHash,
                    ComputeReceiptContentHash(path, worktree, sandboxRoot, skippedProtectionPhases),
                    StringComparison.Ordinal))
            {
                return ReceiptValidation.Invalid;
            }

            var supportedProtectionPhases = skippedProtectionPhases
                .Where(IsSupportedProtectionPhase)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new ReceiptValidation(true, supportedProtectionPhases);
        }
        catch (IOException)
        {
            return ReceiptValidation.Invalid;
        }
        catch (UnauthorizedAccessException)
        {
            return ReceiptValidation.Invalid;
        }
        catch (JsonException)
        {
            return ReceiptValidation.Invalid;
        }
    }

    private static string[] IntersectProtectionPhases(string[]? left, string[]? right)
    {
        if (left is null || right is null)
        {
            return [];
        }

        return left.Intersect(right, StringComparer.Ordinal).ToArray();
    }

    private static bool IsSupportedProtectionPhase(string phase) =>
        string.Equals(phase, ProtectWorkspaceBoundaryPhase, StringComparison.Ordinal) ||
        string.Equals(phase, ProtectGitMetadataPhase, StringComparison.Ordinal);

    private static bool HasRequiredVerificationBasis(string[] verificationBasis) =>
        ReceiptVerificationBasis.All(required => verificationBasis.Contains(required, StringComparer.Ordinal));

    private static void WriteReceipt(
        string path,
        string worktree,
        string sandboxRoot,
        IReadOnlyCollection<string> skippedProtectionPhases)
    {
        var phases = skippedProtectionPhases
            .Where(IsSupportedProtectionPhase)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var receipt = new
        {
            version = ReceiptSchemaVersion,
            path = Path.GetFullPath(path),
            worktree = Path.GetFullPath(worktree),
            sandboxRoot = Path.GetFullPath(sandboxRoot),
            skippedProtectionPhases = phases,
            verificationBasis = ReceiptVerificationBasis,
            contentHash = ComputeReceiptContentHash(path, worktree, sandboxRoot, phases),
            preparedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        File.WriteAllText(ReceiptPath(path), JsonSerializer.Serialize(receipt, JsonOptions) + Environment.NewLine);
    }

    private static string ComputeReceiptContentHash(
        string path,
        string worktree,
        string sandboxRoot,
        IReadOnlyCollection<string> skippedProtectionPhases)
    {
        var phases = skippedProtectionPhases
            .Where(IsSupportedProtectionPhase)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var payload = string.Join(
            "\n",
            $"v{ReceiptSchemaVersion}",
            NormalizePath(path),
            NormalizePath(worktree),
            NormalizePath(sandboxRoot),
            string.Join(",", phases),
            string.Join(",", ReceiptVerificationBasis),
            Directory.GetCreationTimeUtc(worktree).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Directory.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool TryGetStringArray(JsonElement root, string propertyName, out string[] value)
    {
        value = [];
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<string>();
        foreach (var element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var item = element.GetString();
            if (string.IsNullOrWhiteSpace(item))
            {
                return false;
            }

            values.Add(item);
        }

        value = values.ToArray();
        return true;
    }

    private sealed record ReceiptValidation(bool Valid, string[] SkippedProtectionPhases)
    {
        public static ReceiptValidation Invalid { get; } = new(false, []);
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
            var medium = output.Contains("Medium Mandatory Level", StringComparison.OrdinalIgnoreCase);
            var inheritable = output.Contains("(OI)", StringComparison.OrdinalIgnoreCase) &&
                output.Contains("(CI)", StringComparison.OrdinalIgnoreCase);
            return new IntegrityLabelState(Exists: true, low, inheritable, medium);
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
