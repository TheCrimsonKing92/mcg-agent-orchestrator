using System.Runtime.CompilerServices;

public sealed class WorkerGuidanceDisciplineCriteriaRulesTests
{
    private const string ScopeHeading = "## Declaring and forbidding file scopes";
    private const string CriteriaHeading = "## Acceptance criteria that survive refinement";
    private const string CorollariesHeading = "## Corollaries";

    [Xunit.Fact]
    public void AcceptanceCriteriaSectionAppearsBetweenExistingSections()
    {
        var lines = ReadDocumentLines();

        var scopeIndex = FindUniqueHeading(lines, ScopeHeading);
        var criteriaIndex = FindUniqueHeading(lines, CriteriaHeading);
        var corollariesIndex = FindUniqueHeading(lines, CorollariesHeading);

        Xunit.Assert.True(scopeIndex < criteriaIndex && criteriaIndex < corollariesIndex,
            $"Expected {CriteriaHeading} after {ScopeHeading} and before {CorollariesHeading}.");
    }

    [Xunit.Theory]
    [Xunit.InlineData("flat line")]
    [Xunit.InlineData("owner sentence")]
    [Xunit.InlineData("passes unmodified")]
    [Xunit.InlineData("at least one")]
    [Xunit.InlineData("same line")]
    [Xunit.InlineData("wildcard")]
    [Xunit.InlineData("in-process")]
    [Xunit.InlineData(".orchestrator/")]
    public void AcceptanceCriteriaSectionContainsRulePhrase(string phrase)
    {
        var lines = ReadDocumentLines();
        var headingIndex = FindUniqueHeading(lines, CriteriaHeading);
        var nextHeadingIndex = Array.FindIndex(lines, headingIndex + 1,
            line => line.StartsWith("## ", StringComparison.Ordinal));
        var sectionEnd = nextHeadingIndex < 0 ? lines.Length : nextHeadingIndex;
        var section = string.Join("\n", lines[(headingIndex + 1)..sectionEnd]);

        Xunit.Assert.True(section.Contains(phrase, StringComparison.OrdinalIgnoreCase),
            $"Expected {CriteriaHeading} to contain '{phrase}'.");
    }

    private static string[] ReadDocumentLines()
    {
        var path = Path.Combine(FindRepositoryRoot(), "docs", "worker-guidance-discipline.md");
        var document = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        return document.Split('\n');
    }

    private static int FindUniqueHeading(string[] lines, string heading)
    {
        var indices = Enumerable.Range(0, lines.Length)
            .Where(index => lines[index].Equals(heading, StringComparison.Ordinal))
            .ToArray();
        Xunit.Assert.True(indices.Length == 1,
            $"Expected exactly one '{heading}' heading; found {indices.Length}.");
        return indices[0];
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from source file path '{sourceFilePath}'.");
    }
}
