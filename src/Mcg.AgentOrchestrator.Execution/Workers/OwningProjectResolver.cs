using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class OwningProjectResolver
{
    internal static string? FindOwningProject(string worktreeRoot, string absoluteFilePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
        var file = Path.GetFullPath(absoluteFilePath);
        var relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(directory))
            {
                foreach (var project in Directory.EnumerateFiles(directory, "*.csproj")
                    .OrderBy(candidate => candidate, StringComparer.Ordinal))
                {
                    if (Compiles(project, file)) return project;
                }
            }

            if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) break;
            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static bool Compiles(string project, string file)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(project)!, file).Replace('\\', '/');
        var included = Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase);
        XDocument document;
        try { document = XDocument.Load(project); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return included;
        }

        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "Compile"))
        {
            if (Matches((string?)item.Attribute("Remove"), relative)) included = false;
            if (Matches((string?)item.Attribute("Include"), relative)) included = true;
        }

        return included;
    }

    private static bool Matches(string? patterns, string relative)
    {
        if (patterns is null) return false;
        foreach (var entry in patterns.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Contains("$(", StringComparison.Ordinal) ||
                entry.Contains("@(", StringComparison.Ordinal) ||
                entry.Contains("%(", StringComparison.Ordinal)) continue;
            var pattern = entry.Replace('\\', '/');
            while (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];
            if (Regex.IsMatch(relative, GlobToRegex(pattern),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) return true;
        }

        return false;
    }

    private static string GlobToRegex(string pattern)
    {
        var result = new StringBuilder("\\A");
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        result.Append("(?:.*/)?");
                    }
                    else result.Append(".*");
                }
                else result.Append("[^/]*");
            }
            else if (pattern[index] == '?') result.Append("[^/]");
            else result.Append(Regex.Escape(pattern[index].ToString()));
        }

        return result.Append("\\z").ToString();
    }
}
