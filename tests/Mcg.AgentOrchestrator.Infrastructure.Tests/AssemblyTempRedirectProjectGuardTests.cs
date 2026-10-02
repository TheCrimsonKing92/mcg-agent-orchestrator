using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

public sealed class AssemblyTempRedirectProjectGuardTests
{
    private const string RedirectFileName = "AssemblyTempRedirect.cs";

    [Fact]
    public void EveryMtpProjectCompilesAssemblyTempRedirect()
    {
        var testsRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "tests");
        Assert.True(Directory.Exists(testsRoot), $"Could not locate tests directory '{testsRoot}'.");

        var projects = Directory.EnumerateFiles(testsRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(testsRoot, path).Split('/', '\\')
                .Any(segment => new[] { "Fixtures", "bin", "obj" }
                    .Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();
        var errors = new List<string>();
        var mtpProjectCount = 0;
        foreach (var project in projects)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(project.Text);
            }
            catch (XmlException exception)
            {
                errors.Add($"{project.Path} is malformed: {exception.Message}");
                continue;
            }

            if (!document.Descendants().Any(element =>
                    element.Name.LocalName == "PackageReference" &&
                    string.Equals((string?)element.Attribute("Include"), "xunit.v3.mtp-v2",
                        StringComparison.OrdinalIgnoreCase)))
                continue;

            mtpProjectCount++;
            var failure = CheckProject(project.Path, project.Text);
            if (failure is not null)
                errors.Add(failure);
        }

        Assert.True(mtpProjectCount > 0, $"No xunit.v3.mtp-v2 projects found below '{testsRoot}'.");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void MissingAcceptanceRedirectLinkNamesProject()
    {
        var projectPath = Path.Combine(VerifiedRepositoryRoot.Find(), "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "Acceptance",
            "Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.csproj");
        var projectText = File.ReadAllText(projectPath);
        Assert.Null(CheckProject(projectPath, projectText));

        var document = XDocument.Parse(projectText);
        var redirectLinks = document.Descendants()
            .Where(element => element.Name.LocalName == "Compile" &&
                ItemPaths(element, "Include").Any(IsRedirectPath))
            .ToArray();
        Assert.NotEmpty(redirectLinks);
        foreach (var link in redirectLinks)
            link.Remove();

        var failure = CheckProject(projectPath, document.ToString());
        Assert.NotNull(failure);
        Assert.Contains(Path.GetFileName(projectPath), failure);
    }

    internal static string? CheckProject(string projectPath, string csprojText)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(csprojText);
        }
        catch (XmlException exception)
        {
            return $"{projectPath} is malformed: {exception.Message}";
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var compileItems = document.Descendants()
            .Where(element => element.Name.LocalName == "Compile")
            .ToArray();
        var linkedRedirect = compileItems.SelectMany(element => ItemPaths(element, "Include"))
            .Any(path => IsRedirectPath(path) &&
                File.Exists(Path.GetFullPath(Path.Combine(projectDirectory,
                    path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)))));
        var ownedRedirect = File.Exists(Path.Combine(projectDirectory, RedirectFileName)) &&
            !compileItems.SelectMany(element => ItemPaths(element, "Remove"))
                .Any(pattern => MatchesRedirect(pattern));

        return linkedRedirect || ownedRedirect
            ? null
            : $"{projectPath} does not compile {RedirectFileName}.";
    }

    private static IEnumerable<string> ItemPaths(XElement element, string attribute) =>
        ((string?)element.Attribute(attribute) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsRedirectPath(string path) =>
        string.Equals(path.Replace('\\', '/').Split('/')[^1], RedirectFileName,
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesRedirect(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        var regex = "^" + Regex.Escape(normalized)
            .Replace(@"\*\*/", @"(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", @".*", StringComparison.Ordinal)
            .Replace(@"\*", @"[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", @"[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(RedirectFileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
