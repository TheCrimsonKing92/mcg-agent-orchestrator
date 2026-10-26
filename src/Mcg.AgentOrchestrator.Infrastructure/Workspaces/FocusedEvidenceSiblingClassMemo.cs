using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class FocusedEvidenceSiblingClassMemo
{
    internal static FocusedEvidenceSiblingClassMemo Shared { get; } = new();

    private readonly int capacity;
    private readonly Func<string, string> fingerprint;
    private readonly object sync = new();
    private readonly Dictionary<MemoKey, Entry> entries = new();

    internal FocusedEvidenceSiblingClassMemo(int capacity = 256, Func<string, string>? fingerprint = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        this.fingerprint = fingerprint ?? ComputeFingerprint;
    }

    internal IReadOnlyList<string> GetOrCompute(
        string worktreePath,
        string project,
        string requestedClass,
        string projectDirectory,
        Func<IReadOnlyList<string>> compute)
    {
        var key = new MemoKey(Path.GetFullPath(worktreePath), project.Replace('\\', '/'), requestedClass.Trim());
        var currentFingerprint = fingerprint(projectDirectory);
        lock (sync)
        {
            if (entries.TryGetValue(key, out var entry) &&
                entry.Fingerprint.Equals(currentFingerprint, StringComparison.Ordinal))
            {
                return entry.Classes;
            }
        }

        // Fingerprinting and computation stay outside the lock; concurrent misses may both compute.
        var classes = Array.AsReadOnly(compute().ToArray());
        lock (sync)
        {
            if (!entries.ContainsKey(key) && entries.Count >= capacity)
            {
                entries.Clear();
            }

            entries[key] = new Entry(currentFingerprint, classes);
        }

        return classes;
    }

    private static string ComputeFingerprint(string projectDirectory)
    {
        var lines = new DirectoryInfo(projectDirectory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                file.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(file => (File: file, Relative: Path.GetRelativePath(projectDirectory, file.FullName).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(item => string.Create(CultureInfo.InvariantCulture,
                $"{item.Relative}|{item.File.Length}|{item.File.LastWriteTimeUtc.Ticks}"))
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private readonly record struct MemoKey(string WorktreePath, string Project, string RequestedClass);
    private sealed record Entry(string Fingerprint, IReadOnlyList<string> Classes);
}
