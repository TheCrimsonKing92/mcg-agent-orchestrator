using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

public sealed class CaseStudyDocumentationContractTests
{
    private const string PagePath = "docs/case-studies/cohort-attribution-partitions.md";

    [Xunit.Fact]
    public void HeadingsMatchCaseStudyOutline()
    {
        var lines = ReadPageLines(FindRepositoryRoot());

        Xunit.Assert.Equal("# Case study: attribution partitions that silently ran serially", lines[0]);
        Xunit.Assert.Equal(
            new[] { "Context", "Symptom", "Diagnosis", "Fix", "Result", "What it taught" },
            lines.Where(line => line.StartsWith("## ", StringComparison.Ordinal))
                .Select(line => line[3..]));
    }

    [Xunit.Fact]
    public void CitedPathsAndTestClassesResolve()
    {
        var root = FindRepositoryRoot();
        var spans = ReadPageLines(root)
            .SelectMany(line => Regex.Matches(line, "`([^`]+)`", RegexOptions.CultureInvariant)
                .Select(match => match.Groups[1].Value))
            .ToArray();

        var paths = spans.Where(span => span.Contains('/') &&
            (span.EndsWith(".cs", StringComparison.Ordinal) ||
             span.EndsWith(".md", StringComparison.Ordinal))).ToArray();
        Xunit.Assert.NotEmpty(paths);

        var missingPaths = paths.Where(path =>
            Path.IsPathRooted(path) ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            !File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))));
        Xunit.Assert.Empty(missingPaths);

        var classNames = spans.Where(span =>
            span.EndsWith("Tests", StringComparison.Ordinal) ||
            span.StartsWith("AcceptanceCohortWorkflowTests", StringComparison.Ordinal)).ToArray();
        Xunit.Assert.NotEmpty(classNames);

        var testSources = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Select(File.ReadAllText)
            .ToArray();
        var missingClasses = classNames.Where(name => !testSources.Any(source =>
            Regex.IsMatch(source, $@"\bclass\s+{Regex.Escape(name)}\b", RegexOptions.CultureInvariant)));
        Xunit.Assert.Empty(missingClasses);
    }

    [Xunit.Fact]
    public void HeadingMarkersWereTransformed()
    {
        var lines = ReadPageLines(FindRepositoryRoot());

        Xunit.Assert.DoesNotContain(lines, line =>
            line.StartsWith("H1: ", StringComparison.Ordinal) ||
            line.StartsWith("H2: ", StringComparison.Ordinal));
    }

    private static string[] ReadPageLines(string root)
    {
        var path = Path.Combine(root, PagePath.Replace('/', Path.DirectorySeparatorChar));
        Xunit.Assert.True(File.Exists(path), $"Case study page is missing: {PagePath}");
        return File.ReadAllText(path).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }

    private static bool IsGeneratedPath(string path) =>
        path.Replace('\\', '/').Split('/').Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

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

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from source file path '{sourceFilePath}'.");
    }
}
