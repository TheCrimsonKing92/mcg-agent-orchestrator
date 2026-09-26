using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record DeveloperDeferredTestSelection(
    IReadOnlyList<FindingEvidenceSelection> Selections,
    IReadOnlyList<string> NotRun);

internal static class DeveloperDeferredTestSelections
{
    private static readonly Regex ClassToken = new(
        @"\b[A-Z][A-Za-z0-9_]*\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static DeveloperDeferredTestSelection Resolve(string worktreePath, string testsField)
    {
        var deferred = testsField.IndexOf("deferred", StringComparison.OrdinalIgnoreCase);
        if (deferred < 0) return new DeveloperDeferredTestSelection([], []);
        var declaration = testsField[(deferred + "deferred".Length)..];
        var names = ClassToken.Matches(declaration).Select(match => match.Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        var root = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(root)) return new DeveloperDeferredTestSelection([], names);

        var projects = Directory.EnumerateDirectories(root)
            .Where(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any())
            .ToArray();
        var selections = new List<FindingEvidenceSelection>();
        var notRun = new List<string>();
        foreach (var name in names)
        {
            var matches = projects.SelectMany(project =>
                    Directory.EnumerateFiles(project, name + ".cs", SearchOption.AllDirectories)
                        .Where(path => !path.Split(Path.DirectorySeparatorChar)
                            .Any(part => part is "bin" or "obj"))
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
