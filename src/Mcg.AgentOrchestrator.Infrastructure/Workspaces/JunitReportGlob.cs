using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Case-sensitive project-relative report selection. Report inputs are never modified.</summary>
internal static class JunitReportGlob
{
    internal static bool Matches(string glob, string path) => Matcher(glob).IsMatch(path.Replace('\\', '/'));

    internal static IReadOnlyList<string> FreshFiles(string projectRoot, IReadOnlyList<string> globs,
        DateTimeOffset started, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var glob in globs)
        {
            var normalized = Normalize(glob);
            var matcher = Matcher(normalized);
            var parts = normalized.Split('/');
            var prefix = parts.Take(parts.Length - 1).TakeWhile(part => part.IndexOfAny(['*', '?']) < 0);
            var directory = Path.Combine(projectRoot, string.Join(Path.DirectorySeparatorChar.ToString(), prefix));
            if (!Directory.Exists(directory)) continue;
            var active = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            Visit(directory);

            void Visit(string current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var physical = CrosswalkAcceptanceCheck.ResolveCrosswalkProjectRoot(projectRoot, Path.GetRelativePath(projectRoot, current));
                if (!active.Add(physical)) throw new IOException($"JUnit report directory link cycle: {current}");
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.Device) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            // A non-recursive glob cannot match below its literal directory.
                            if (normalized.Contains("**", StringComparison.Ordinal) ||
                                Path.GetRelativePath(projectRoot, entry).Replace('\\', '/').Count(c => c == '/') < parts.Length - 1)
                                Visit(entry);
                            continue;
                        }
                        var relative = Path.GetRelativePath(projectRoot, entry).Replace('\\', '/');
                        if (!matcher.IsMatch(relative)) continue;
                        EnsureContainedFile(projectRoot, entry);
                        if (File.GetLastWriteTimeUtc(entry) >= started.UtcDateTime) found.TryAdd(relative, entry);
                    }
                }
                finally { active.Remove(physical); }
            }
        }
        return found.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value).ToArray();
    }

    internal static void EnsureContainedFile(string projectRoot, string file)
    {
        var parent = CrosswalkAcceptanceCheck.ResolveCrosswalkProjectRoot(projectRoot,
            Path.GetRelativePath(projectRoot, Path.GetDirectoryName(file)!));
        var info = new FileInfo(Path.Combine(parent, Path.GetFileName(file)));
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var target = info.ResolveLinkTarget(true) ?? throw new IOException($"Unresolved JUnit report link: {file}");
            if (target is not FileInfo) throw new IOException($"JUnit report is not a regular file: {file}");
            _ = CrosswalkAcceptanceCheck.ResolveCrosswalkProjectRoot(projectRoot,
                Path.GetRelativePath(projectRoot, Path.GetDirectoryName(target.FullName)!));
        }
    }

    private static Regex Matcher(string glob)
    {
        var pattern = new StringBuilder("\\A");
        var normalized = Normalize(glob);
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (character == '*' && index + 1 < normalized.Length && normalized[index + 1] == '*')
            {
                index++;
                if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                {
                    index++;
                    pattern.Append("(.*/)?");
                }
                else pattern.Append(".*");
            }
            else pattern.Append(character switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(character.ToString()) });
        }
        return new(pattern.Append("\\z").ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static string Normalize(string glob) => string.Join("/", glob.Replace('\\', '/').Split('/')
        .Where(part => part is not ("" or ".")));
}
