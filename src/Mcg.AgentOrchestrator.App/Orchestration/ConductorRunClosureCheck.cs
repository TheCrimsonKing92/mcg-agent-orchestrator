using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorRunClosureVerdict(
    bool Intact, string Folder, int? Expected, int? Actual, string FirstMissing, string Reason)
{
    internal IReadOnlyCollection<string>? VerifiedInventory { get; init; }
}

// A successful check establishes launch eligibility only for this observation. Inventories
// may be retained for diagnostics, but a previous verdict must never authorize another launch.
internal static class ConductorRunClosureCheck
{
    internal const string MarkerName = ".mcg-run-closure.json";

    internal static ConductorRunClosureVerdict Verify(
        string runDirectory, string appLeaf, IReadOnlyCollection<string>? inventory = null)
    {
        int? expected = null;
        int? actual = null;
        string[]? files = null;
        var markerUsable = false;
        var phase = "folder-missing";
        ConductorRunClosureVerdict Failed(string reason)
        {
            if (!markerUsable)
                return new(false, runDirectory, null, actual, "unavailable", reason);
            if (inventory is null)
                return new(false, runDirectory, expected, actual, "unavailable", reason + ":inventory-unavailable");
            var present = files is null ? null : new HashSet<string>(
                files.Select(file => RelativePath(runDirectory, file)), StringComparer.OrdinalIgnoreCase);
            if (present is null)
                return new(false, runDirectory, expected, actual, "unavailable", reason);
            var missing = inventory.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(path => !present.Contains(path));
            return new(false, runDirectory, expected, actual, missing ?? "none",
                missing is not null && reason == "digest-mismatch" ? "files-missing" : reason);
        }

        try
        {
            if (!Directory.Exists(runDirectory)) return Failed(phase);
            phase = "enumeration-failed";
            files = EnumerateFiles(runDirectory);
            actual = files.Length;

            phase = "marker-missing";
            var markerPath = Path.Combine(runDirectory, MarkerName);
            if (!File.Exists(markerPath)) return Failed(phase);
            phase = "marker-malformed";
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = marker.RootElement;
            if (ReadInteger(root, "schemaVersion") != 2) return Failed("marker-unsupported");
            var digest = root.GetProperty("digest").GetString();
            if (string.IsNullOrEmpty(digest)) return Failed(phase);
            expected = ReadInteger(root, "fileCount");
            markerUsable = true;

            if (!File.Exists(Path.Combine(runDirectory, appLeaf))) return Failed("app-missing");
            if (!HasNativeSqlite(runDirectory)) return Failed("sqlite-missing");

            phase = "enumeration-failed";
            var payload = new StringBuilder();
            var observedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var relative = RelativePath(runDirectory, file);
                if (!observedFiles.Add(relative)) return Failed("duplicate-relative-path");
                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                payload.Append(relative).Append(':')
                    .Append(Convert.ToHexString(SHA256.HashData(stream))).Append('\n');
            }
            var observedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
            return expected == actual && string.Equals(digest, observedDigest, StringComparison.Ordinal)
                ? new(true, runDirectory, expected, actual, "none", "intact") { VerifiedInventory = observedFiles.ToArray() }
                : Failed("digest-mismatch");
        }
        catch (Exception)
        {
            return Failed(phase);
        }
    }

    internal static IReadOnlyList<string> EnumerateRelativePaths(string directory) =>
        EnumerateFiles(directory).Select(file => RelativePath(directory, file)).ToArray();

    private static string[] EnumerateFiles(string directory) => Directory
        .EnumerateFiles(Path.GetFullPath(directory), "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false
        })
        .Where(file => !RelativePath(directory, file).Equals(MarkerName, StringComparison.Ordinal))
        .OrderBy(file => file, Comparer<string>.Create(CompareFullPaths)).ToArray();

    private static string RelativePath(string directory, string file) =>
        Path.GetRelativePath(Path.GetFullPath(directory), file).Replace('\\', '/');

    private static int ReadInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        return value.ValueKind == JsonValueKind.String
            ? int.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetInt32();
    }

    private static bool HasNativeSqlite(string directory) =>
        Directory.EnumerateFiles(directory, "e_sqlite3.*", new EnumerationOptions { AttributesToSkip = 0 }).Any() ||
        (Directory.Exists(Path.Combine(directory, "runtimes")) && Directory.EnumerateFiles(
            Path.Combine(directory, "runtimes"), "e_sqlite3.*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }).Any());

    private static int CompareFullPaths(string? left, string? right)
    {
        if (!OperatingSystem.IsWindows())
            return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        // resolve-run-dir.ps1 runs in Windows PowerShell (.NET Framework/NLS).
        // Use its linguistic, case-insensitive ordering even when this runtime uses ICU.
        var result = CompareStringEx(CultureInfo.CurrentCulture.Name, 0x08000001,
            left!, left!.Length, right!, right!.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (result == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result - 2;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int CompareStringEx(string localeName, uint flags,
        string left, int leftLength, string right, int rightLength,
        IntPtr versionInformation, IntPtr reserved, IntPtr parameter);
}
