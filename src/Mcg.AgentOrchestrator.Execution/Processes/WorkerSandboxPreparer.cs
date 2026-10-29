using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

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

internal sealed record IntegrityLabelState(
    bool Exists,
    bool Low,
    bool Inheritable,
    bool Medium = false,
    int? NativeQueryError = null,
    bool Unlabeled = false);

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

    internal CodexScratchScope OpenCodexScratch(string worktree, string sandboxRoot) =>
        new(worktree, sandboxRoot, labeler);

    // Authority is invocation-local and handle-backed. Neither a receipt nor CODEX_HOME grants it.
    internal sealed class CodexScratchScope : IDisposable
    {
        internal const int MaximumEntries = 4096;
        internal const int MaximumDepth = 32;
        private readonly Dictionary<string, HeldEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly IWorkerIntegrityLabeler integrity;
        private readonly string owner;
        private readonly string sandbox;
        private readonly string home;
        private readonly List<string> targets = [];

        internal CodexScratchScope(string worktree, string sandboxRoot, IWorkerIntegrityLabeler integrity)
        {
            this.integrity = integrity;
            owner = WindowsIdentity.GetCurrent().User?.Value
                ?? throw Refused(worktree, "owner-unavailable");
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
            sandbox = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxRoot));
            var boundary = Path.Combine(workspace, ".mcg-sandbox");
            if (!sandbox.Equals(boundary, StringComparison.OrdinalIgnoreCase) &&
                !sandbox.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw Refused(sandbox, "outside-generated-sandbox");
            home = Path.Combine(sandbox, "codex-home");
            try
            {
                // Lock every ancestor, including the workspace, before touching generated paths.
                HoldAncestors(workspace);
                var relative = Path.GetRelativePath(workspace, sandbox);
                var current = workspace;
                foreach (var part in relative.Split(Path.DirectorySeparatorChar))
                {
                    current = Path.Combine(current, part);
                    EnsureDirectory(current);
                }
                // This is the existing selected-home materialization, under the retained boundary.
                EnsureDirectory(home);
                CaptureTargets();
            }
            catch (Exception failure)
            {
                Dispose();
                if (failure is WorkerSandboxRefusedException) throw;
                throw Refused(home, "safety-inspection-failed", failure);
            }
        }

        internal void ValidateAndRepair()
        {
            try { ValidateCore(); }
            catch (Exception failure) when (failure is not WorkerSandboxRefusedException)
            {
                throw Refused(home, "validation-failed", failure);
            }
        }

        private void ValidateCore()
        {
            CaptureTargets(); // Includes children created by provider materialization.
            VerifyIdentities();
            foreach (var path in targets)
            {
                var directory = (entries[path].Identity.Attributes & (uint)FileAttributes.Directory) != 0;
                var state = integrity.Query(path);
                if (!state.Exists || state.NativeQueryError is not null)
                    throw Refused(path, $"label-query-failed-native-{state.NativeQueryError}");
                if (state.Low && (!directory || state.Inheritable)) continue;
                if (!state.Low && !state.Medium && !state.Unlabeled)
                    throw Refused(path, "label-unverifiable");
                VerifyIdentities();
                var level = directory ? LowInheritableLevel : "L";
                if (integrity is IcaclsIntegrityLabeler native)
                {
                    var result = native.SetIntegrityWithDiagnostics(path, level, recursive: false);
                    if (!result.Succeeded)
                        throw Refused(path, "label-repair-failed", new InvalidOperationException(result.Diagnostic));
                }
                else if (!integrity.SetIntegrity(path, level, recursive: false))
                    throw Refused(path, "label-repair-failed");
                VerifyIdentities();
                state = integrity.Query(path);
                if (!state.Exists || !state.Low || (directory && !state.Inheritable) || state.NativeQueryError is not null)
                    throw Refused(path, $"label-readback-failed-native-{state.NativeQueryError}");
            }
            VerifyIdentities();
        }

        private void CaptureTargets()
        {
            targets.Clear();
            targets.Add(home);
            foreach (var name in new[] { "tmp", "sessions" })
                Visit(Path.Combine(home, name), 0);
        }

        private void Visit(string path, int depth)
        {
            if (depth > MaximumDepth || targets.Count >= MaximumEntries)
                throw Refused(path, "scratch-budget-exceeded");
            var held = Hold(path, owned: true, allowMissing: true);
            if (held is null) return; // Nearest existing directory is checked for Low inheritance.
            if ((depth == 0 || path.Equals(Path.Combine(home, "tmp", "arg0"), StringComparison.OrdinalIgnoreCase)) &&
                (held.Identity.Attributes & (uint)FileAttributes.Directory) == 0)
                throw Refused(path, "expected-directory");
            targets.Add(path);
            if ((held.Identity.Attributes & (uint)FileAttributes.Directory) == 0) return;
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                Visit(child, depth + 1);
        }

        private void HoldAncestors(string path)
        {
            var ancestors = new Stack<string>();
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if (ancestors.Count >= 128) throw Refused(path, "ancestor-budget-exceeded");
                ancestors.Push(current);
            }
            foreach (var ancestor in ancestors)
                _ = Hold(ancestor, owned: false, allowMissing: false);
        }

        private void EnsureDirectory(string path)
        {
            var held = Hold(path, owned: true, allowMissing: true);
            if (held is null)
            {
                Directory.CreateDirectory(path);
                held = Hold(path, owned: true, allowMissing: false)!;
            }
            if ((held.Identity.Attributes & (uint)FileAttributes.Directory) == 0)
                throw Refused(path, "expected-directory");
        }

        private HeldEntry? Hold(string path, bool owned, bool allowMissing)
        {
            if (entries.TryGetValue(path, out var existing)) return existing;
            // FILE_READ_DATA (FILE_LIST_DIRECTORY for directories) makes this open participate in
            // Windows sharing checks; metadata-only access does not prevent file replacement.
            // No contents are read. Deny write/delete sharing while path-based label APIs execute.
            // Handles are non-inheritable and survive through startup.
            var handle = CreateFile(IcaclsIntegrityLabeler.NativeFilePath(path), 0x20081, 1,
                IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (allowMissing && error is 2 or 3) return null;
                throw Refused(path, $"identity-open-failed-native-{error}");
            }
            try
            {
                if (!GetFileInformationByHandle(handle, out var identity))
                    throw Refused(path, $"identity-query-failed-native-{Marshal.GetLastWin32Error()}");
                if ((identity.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw Refused(path, "reparse-target");
                if (owned && (identity.Attributes & (uint)FileAttributes.Directory) == 0 && identity.Links != 1)
                    throw Refused(path, "hardlinked-target");
                if (owned) VerifyOwner(handle, path);
                var entry = new HeldEntry(handle, identity, owned);
                entries.Add(path, entry);
                return entry;
            }
            catch { handle.Dispose(); throw; }
        }

        private void VerifyOwner(SafeFileHandle handle, string path)
        {
            var result = GetSecurityInfo(handle, 1, 1, out var sid, out _, out _, out _, out var descriptor);
            try
            {
                if (result != 0 || sid == IntPtr.Zero || new SecurityIdentifier(sid).Value != owner)
                    throw Refused(path, $"owner-mismatch-native-{result}");
            }
            finally { if (descriptor != IntPtr.Zero) _ = ScratchLocalFree(descriptor); }
        }

        private void VerifyIdentities()
        {
            foreach (var (path, entry) in entries)
            {
                if (!GetFileInformationByHandle(entry.Handle, out var current) ||
                    current.Volume != entry.Identity.Volume || current.IndexHigh != entry.Identity.IndexHigh ||
                    current.IndexLow != entry.Identity.IndexLow || current.Attributes != entry.Identity.Attributes ||
                    current.Links != entry.Identity.Links)
                    throw Refused(path, "identity-changed");
                if (entry.Owned) VerifyOwner(entry.Handle, path);
            }
        }

        private static WorkerSandboxRefusedException Refused(string path, string check, Exception? inner = null) =>
            new($"codex-scratch-{check};remediation=recreate-owned-sandbox-before-retry", path, inner);

        public void Dispose()
        {
            foreach (var entry in entries.Values.Reverse()) entry.Handle.Dispose();
            entries.Clear();
        }

        private sealed record HeldEntry(SafeFileHandle Handle, ScratchFileIdentity Identity, bool Owned);
        [StructLayout(LayoutKind.Sequential)]
        private struct ScratchFileIdentity
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ScratchFileIdentity information);
        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint information,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
        private static extern IntPtr ScratchLocalFree(IntPtr memory);
    }

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
                PrepReceiptHit: true,
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
    private const uint LabelSecurityInformation = 0x00000010;
    private const int SeFileObject = 1;
    private const int ErrorSuccess = 0;
    private const int NativeQueryLibraryUnavailable = -1;
    private const int NativeQueryEntryPointUnavailable = -2;
    private const int NativeQueryUnhandledFailure = -3;
    private const int AclHeaderSize = 8;
    private const int AceHeaderSize = 4;
    private const int MandatoryLabelAceType = 0x11;
    private const byte ObjectInheritAce = 0x01;
    private const byte ContainerInheritAce = 0x02;
    private const byte InheritOnlyAce = 0x08;
    private const uint NoWriteUp = 0x00000001;
    private const uint LowIntegrityRid = 0x00001000;
    private const uint MediumIntegrityRid = 0x00002000;
    private const int MaximumAclBytes = 64 * 1024;

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
        if (!OperatingSystem.IsWindows())
        {
            return new IntegrityLabelState(Exists: false, Low: false, Inheritable: false);
        }

        IntPtr owner = IntPtr.Zero;
        IntPtr group = IntPtr.Zero;
        IntPtr discretionaryAcl = IntPtr.Zero;
        IntPtr systemAcl = IntPtr.Zero;
        IntPtr securityDescriptor = IntPtr.Zero;
        try
        {
            var result = GetNamedSecurityInfo(
                NativeFilePath(path),
                SeFileObject,
                LabelSecurityInformation,
                out owner,
                out group,
                out discretionaryAcl,
                out systemAcl,
                out securityDescriptor);
            if (result != ErrorSuccess)
            {
                return NativeQueryFailed(checked((int)result));
            }

            if (systemAcl == IntPtr.Zero)
            {
                // A successful query with no label means implicit Medium, not failed readback.
                return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false, Unlabeled: true);
            }
            if (!TryCopyAcl(systemAcl, out var acl))
            {
                return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
            }

            return DecodeMandatoryLabelAcl(acl);
        }
        catch (DllNotFoundException)
        {
            return NativeQueryFailed(NativeQueryLibraryUnavailable);
        }
        catch (EntryPointNotFoundException)
        {
            return NativeQueryFailed(NativeQueryEntryPointUnavailable);
        }
        catch
        {
            return NativeQueryFailed(NativeQueryUnhandledFailure);
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }
        }
    }

    internal static IntegrityLabelState DecodeMandatoryLabelAcl(ReadOnlySpan<byte> acl)
    {
        if (acl.Length < AclHeaderSize || (acl[0] is not 2 and not 4))
        {
            return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        }

        var aclSize = BinaryPrimitives.ReadUInt16LittleEndian(acl.Slice(2, sizeof(ushort)));
        var aceCount = BinaryPrimitives.ReadUInt16LittleEndian(acl.Slice(4, sizeof(ushort)));
        if (aclSize != acl.Length || aclSize < AclHeaderSize)
        {
            return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        }

        var offset = AclHeaderSize;
        var mandatoryLabelCount = 0;
        var labelState = new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        for (var index = 0; index < aceCount; index++)
        {
            if (offset > acl.Length - AceHeaderSize)
            {
                return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
            }

            var aceType = acl[offset];
            var aceFlags = acl[offset + 1];
            var aceSize = BinaryPrimitives.ReadUInt16LittleEndian(acl.Slice(offset + 2, sizeof(ushort)));
            if (aceSize < AceHeaderSize || aceSize > acl.Length - offset)
            {
                return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
            }

            if (aceType == MandatoryLabelAceType)
            {
                mandatoryLabelCount++;
                if (mandatoryLabelCount != 1 || !TryDecodeMandatoryLabelAce(acl.Slice(offset, aceSize), aceFlags, out labelState))
                {
                    return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
                }
            }

            offset += aceSize;
        }

        if (offset != acl.Length)
            return new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        return mandatoryLabelCount == 1 ? labelState
            : new IntegrityLabelState(Exists: true, Low: false, Inheritable: false, Unlabeled: true);
    }

    private static IntegrityLabelState NativeQueryFailed(int errorCode) =>
        new(Exists: false, Low: false, Inheritable: false, NativeQueryError: errorCode);

    private static bool TryCopyAcl(IntPtr systemAcl, out byte[] acl)
    {
        acl = [];
        var header = new byte[AclHeaderSize];
        Marshal.Copy(systemAcl, header, 0, header.Length);
        var aclSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, sizeof(ushort)));
        if (aclSize < AclHeaderSize || aclSize > MaximumAclBytes)
        {
            return false;
        }

        acl = new byte[aclSize];
        Marshal.Copy(systemAcl, acl, 0, acl.Length);
        return true;
    }

    private static bool TryDecodeMandatoryLabelAce(ReadOnlySpan<byte> ace, byte aceFlags, out IntegrityLabelState state)
    {
        state = new IntegrityLabelState(Exists: true, Low: false, Inheritable: false);
        if (ace.Length < 12 || (aceFlags & InheritOnlyAce) != 0)
        {
            return false;
        }

        var mask = BinaryPrimitives.ReadUInt32LittleEndian(ace.Slice(AceHeaderSize, sizeof(uint)));
        if ((mask & NoWriteUp) == 0)
        {
            return false;
        }

        var sid = ace.Slice(AceHeaderSize + sizeof(uint));
        if (!TryReadIntegrityRid(sid, out var integrityRid))
        {
            return false;
        }

        // Later-created tmp/arg0 needs transitive inheritance, not just a one-generation label.
        var inheritable = (aceFlags & (ObjectInheritAce | ContainerInheritAce)) ==
            (ObjectInheritAce | ContainerInheritAce) && (aceFlags & 0x04) == 0;
        state = integrityRid switch
        {
            LowIntegrityRid => new IntegrityLabelState(Exists: true, Low: true, Inheritable: inheritable),
            MediumIntegrityRid => new IntegrityLabelState(Exists: true, Low: false, Inheritable: inheritable, Medium: true),
            _ => new IntegrityLabelState(Exists: true, Low: false, Inheritable: false)
        };
        return integrityRid is LowIntegrityRid or MediumIntegrityRid;
    }

    private static bool TryReadIntegrityRid(ReadOnlySpan<byte> sid, out uint integrityRid)
    {
        integrityRid = 0;
        if (sid.Length < 8 || sid[0] != 1 || sid[1] != 1)
        {
            return false;
        }

        var sidLength = 8 + (sid[1] * sizeof(uint));
        if (sid.Length != sidLength)
        {
            return false;
        }

        ReadOnlySpan<byte> mandatoryLabelAuthority = [0, 0, 0, 0, 0, 16];
        if (!sid.Slice(2, 6).SequenceEqual(mandatoryLabelAuthority))
        {
            return false;
        }

        integrityRid = BinaryPrimitives.ReadUInt32LittleEndian(sid.Slice(8, sizeof(uint)));
        return true;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfo(
        string objectName,
        int objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    // Win32 security APIs and icacls do not inherit .NET's long-path handling.
    // Resolve ordinary paths before adding the extended prefix so relative segments
    // and forward slashes retain their normal filesystem meaning.
    // A short recursive root can contain descendants beyond MAX_PATH.
    internal static string NativeFilePath(string path, bool recursive = false)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length < 260 && !recursive)
        {
            return path;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    public bool SetIntegrity(string path, string level, bool recursive) =>
        SetIntegrityWithDiagnostics(path, level, recursive).Succeeded;

    internal IntegrityLabelCommandResult SetIntegrityWithDiagnostics(string path, string level, bool recursive)
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
            psi.ArgumentList.Add(NativeFilePath(path, recursive));
            psi.ArgumentList.Add("/setintegritylevel");
            psi.ArgumentList.Add(level);
            if (recursive)
            {
                psi.ArgumentList.Add("/T");
            }

            using var process = startProcess(psi);
            if (process is null)
            {
                return IntegrityLabelCommandResult.Failed(null);
            }

            var output = new IntegrityLabelCommandResult.OutputCapture(keepTail: true);
            var error = new IntegrityLabelCommandResult.OutputCapture(keepTail: false);
            var copyOut = output.DrainAsync(process.StandardOutput);
            var copyErr = error.DrainAsync(process.StandardError);
            var completed = DispatchProcessHost.WaitForIntegrityLabeler(process, TimeSpan.FromMinutes(2));
            try { Task.WaitAll([copyOut, copyErr], 2000); } catch { }
            return IntegrityLabelCommandResult.Completed(completed, process.HasExited ? process.ExitCode : null,
                copyOut.IsCompletedSuccessfully && copyErr.IsCompletedSuccessfully, output, error);
        }
        catch (Exception exception)
        {
            return IntegrityLabelCommandResult.Failed(exception);
        }
    }
}
