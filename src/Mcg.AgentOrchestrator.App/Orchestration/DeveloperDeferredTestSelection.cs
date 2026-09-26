using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record DeveloperDeferredTestSelection(
    IReadOnlyList<FindingEvidenceSelection> Selections,
    IReadOnlyList<string> NotRun);

internal static class DeveloperDeferredTestSelections
{
    private static readonly Regex ClassToken = new(
        @"^[A-Z][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static DeveloperDeferredTestSelection Resolve(string worktreePath, string testsField)
    {
        var deferred = testsField.IndexOf("deferred", StringComparison.OrdinalIgnoreCase);
        if (deferred < 0) return new DeveloperDeferredTestSelection([], []);
        var declaration = testsField[(deferred + "deferred".Length)..].TrimStart(' ', ':', '-');
        // A declaration is a comma-separated list. Stop at prose or another field.
        declaration = declaration.Split([';', '\r', '\n'], 2)[0];
        var names = declaration.Split(',', StringSplitOptions.TrimEntries)
            .TakeWhile(name => ClassToken.IsMatch(name))
            .Distinct(StringComparer.Ordinal).ToArray();
        var root = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(root)) return new DeveloperDeferredTestSelection([], names);

        var projects = Directory.EnumerateDirectories(root)
            .Where(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any())
            .ToArray();
        var sourceFiles = projects.ToDictionary(
            project => project,
            project => EnumerateSourceFiles(project).ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var selections = new List<FindingEvidenceSelection>();
        var notRun = new List<string>();
        foreach (var name in names)
        {
            var matches = projects.SelectMany(project =>
                    sourceFiles[project][name + ".cs"]
                        .Where(path => DeclaresClass(path, name))
                        .Select(_ => project))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length != 1)
            {
                notRun.Add(name);
                continue;
            }
            selections.Add(new FindingEvidenceSelection(Path.GetFileName(matches[0]), name));
        }
        return new DeveloperDeferredTestSelection(selections, notRun);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
                yield return path;
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(child);
                if (name is "bin" or "obj" ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(child);
            }
        }
    }

    private static bool DeclaresClass(string path, string name)
    {
        try
        {
            return Regex.IsMatch(File.ReadAllText(path),
                @"\b(?:class|record|struct)\s+" + Regex.Escape(name) + @"\b",
                RegexOptions.CultureInvariant);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
