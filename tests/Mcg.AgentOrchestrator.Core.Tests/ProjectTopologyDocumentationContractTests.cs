using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public sealed class ProjectTopologyDocumentationContractTests
{
    private const string InventoryBegin = "<!-- current-project-inventory:begin -->";
    private const string InventoryEnd = "<!-- current-project-inventory:end -->";
    private const string ReadmePath = "README.md";
    private const string ArchitecturePath = "docs/architecture.md";

    [Xunit.Fact]
    public void CurrentInventoriesAndGraphMatchProjects()
    {
        var root = FindRepositoryRoot();
        Validate(root, ReadDocuments(root));
    }

    [Xunit.Fact]
    public void ReadmeOmissionIsRejected()
    {
        var root = FindRepositoryRoot();
        var documents = ReadDocuments(root);
        var project = DiscoverProjects(root)[0];
        var mutated = RemoveProjectRow(documents.Readme, project);
        Xunit.Assert.NotEqual(documents.Readme, mutated);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(root, documents with { Readme = mutated }));

        Xunit.Assert.Equal(
            $"{ReadmePath} current project inventory is missing project '{project}'.",
            exception.Message);
    }

    [Xunit.Fact]
    public void ReadmeInventionIsRejected()
    {
        var root = FindRepositoryRoot();
        var documents = ReadDocuments(root);
        const string invented = "src/Invented/Invented.csproj";
        var mutated = AddProjectRow(documents.Readme, invented);
        Xunit.Assert.NotEqual(documents.Readme, mutated);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(root, documents with { Readme = mutated }));

        Xunit.Assert.Equal(
            $"{ReadmePath} current project inventory invents project '{invented}'.",
            exception.Message);
    }

    [Xunit.Fact]
    public void ArchitectureOmissionIsRejected()
    {
        var root = FindRepositoryRoot();
        var documents = ReadDocuments(root);
        var project = DiscoverProjects(root)[0];
        var mutated = RemoveProjectRow(documents.Architecture, project);
        Xunit.Assert.NotEqual(documents.Architecture, mutated);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(root, documents with { Architecture = mutated }));

        Xunit.Assert.Equal(
            $"{ArchitecturePath} current project inventory is missing project '{project}'.",
            exception.Message);
    }

    [Xunit.Fact]
    public void ArchitectureInventionIsRejected()
    {
        var root = FindRepositoryRoot();
        var documents = ReadDocuments(root);
        const string invented = "src/Invented/Invented.csproj";
        var mutated = AddProjectRow(documents.Architecture, invented);
        Xunit.Assert.NotEqual(documents.Architecture, mutated);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(root, documents with { Architecture = mutated }));

        Xunit.Assert.Equal(
            $"{ArchitecturePath} current project inventory invents project '{invented}'.",
            exception.Message);
    }

    [Xunit.Fact]
    public void DuplicateInventoryRowIsRejected()
    {
        var root = FindRepositoryRoot();
        var documents = ReadDocuments(root);
        var project = DiscoverProjects(root)[0];
        var mutated = DuplicateProjectRow(documents.Readme, project);
        Xunit.Assert.NotEqual(documents.Readme, mutated);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(root, documents with { Readme = mutated }));

        Xunit.Assert.Equal(
            $"{ReadmePath} current project inventory repeats project '{project}'.",
            exception.Message);
    }

    [Xunit.Fact]
    public void ProductionReferenceDriftIsRejected()
    {
        var root = FindRepositoryRoot();
        var documents = ReadDocuments(root);
        var mutation = RemoveProductionReference(root, documents.Architecture);
        Xunit.Assert.NotEqual(documents.Architecture, mutation.Architecture);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(
            () => Validate(root, documents with { Architecture = mutation.Architecture }));

        Xunit.Assert.Equal(
            $"{ArchitecturePath} direct references for '{mutation.Project}' differ from its csproj; " +
            $"missing [{mutation.Reference}], invented [].",
            exception.Message);
    }

    private static void Validate(string root, Documentation documents)
    {
        var projects = DiscoverProjects(root);
        ValidateInventory(ReadmePath, documents.Readme, projects, minimumDetailColumns: 2);
        ValidateInventory(ArchitecturePath, documents.Architecture, projects, minimumDetailColumns: 3);
        ValidateProductionGraph(root, documents.Architecture, projects);

        RequireContains(documents.Readme, "](docs/architecture.md)",
            "README.md must link to docs/architecture.md.");
        foreach (var target in new[]
        {
            "operator-runbook.md",
            "repository-conventions.md",
            "role-capability-matrix.md",
            "test-design-discipline.md",
            "dispositive-decision-discipline.md"
        })
        {
            RequireContains(documents.Architecture, $"]({target})",
                $"{ArchitecturePath} must link to {target}.");
        }
    }

    private static void ValidateInventory(
        string documentPath,
        string document,
        IReadOnlyList<string> discoveredProjects,
        int minimumDetailColumns)
    {
        var rows = ParseInventoryRows(documentPath, document);
        var duplicates = rows
            .GroupBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"{documentPath} current project inventory repeats project '{duplicates[0]}'.");
        }

        var documented = rows
            .Select(row => row.Project)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = discoveredProjects
            .Where(project => !documented.Contains(project))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"{documentPath} current project inventory is missing project '{missing[0]}'.");
        }

        var discovered = discoveredProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invented = documented
            .Where(project => !discovered.Contains(project))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (invented.Length > 0)
        {
            throw new InvalidOperationException(
                $"{documentPath} current project inventory invents project '{invented[0]}'.");
        }

        foreach (var row in rows)
        {
            if (row.DetailColumns.Count < minimumDetailColumns ||
                row.DetailColumns.Take(minimumDetailColumns).Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"{documentPath} project '{row.Project}' must state its responsibility and explicit non-responsibility.");
            }
        }
    }

    private static void ValidateProductionGraph(
        string root,
        string architecture,
        IReadOnlyList<string> projects)
    {
        var production = projects
            .Where(project => project.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = ParseInventoryRows(ArchitecturePath, architecture)
            .Where(row => production.Contains(row.Project))
            .ToDictionary(row => row.Project, StringComparer.OrdinalIgnoreCase);

        foreach (var project in production)
        {
            var projectFile = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            var expected = XDocument.Load(projectFile, LoadOptions.None)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Normalize(Path.GetRelativePath(
                    root,
                    Path.GetFullPath(Path.Combine(projectDirectory, include!)))))
                .Where(production.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var documented = Regex.Matches(
                    rows[project].DetailColumns[0],
                    @"`(?<project>src/[^`]+\.csproj)`",
                    RegexOptions.CultureInvariant)
                .Select(match => match.Groups["project"].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = expected.Where(reference => !documented.Contains(reference)).ToArray();
            var invented = documented.Where(reference => !expected.Contains(reference)).ToArray();
            if (missing.Length > 0 || invented.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{ArchitecturePath} direct references for '{project}' differ from its csproj; " +
                    $"missing [{string.Join(", ", missing)}], invented [{string.Join(", ", invented)}].");
            }
        }
    }

    private static IReadOnlyList<string> DiscoverProjects(string root) =>
        new[] { "src", "tests" }
            .SelectMany(scope => Directory.EnumerateFiles(
                Path.Combine(root, scope),
                "*.csproj",
                SearchOption.AllDirectories))
            .Where(path => !IsGeneratedPath(path))
            .Select(path => Normalize(Path.GetRelativePath(root, path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<InventoryRow> ParseInventoryRows(string path, string document)
    {
        var section = ExtractInventory(path, document);
        return Regex.Matches(
                section,
                @"^\|\s*`(?<project>(?:src|tests)/[^`]+\.csproj)`\s*\|(?<details>.*)\|\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => new InventoryRow(
                match.Groups["project"].Value,
                match.Groups["details"].Value
                    .Split('|', StringSplitOptions.TrimEntries)
                    .ToArray()))
            .ToArray();
    }

    private static string ExtractInventory(string path, string document)
    {
        var begin = document.IndexOf(InventoryBegin, StringComparison.Ordinal);
        var end = document.IndexOf(InventoryEnd, StringComparison.Ordinal);
        if (begin < 0 || end <= begin ||
            document.IndexOf(InventoryBegin, begin + InventoryBegin.Length, StringComparison.Ordinal) >= 0 ||
            document.IndexOf(InventoryEnd, end + InventoryEnd.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"{path} must contain exactly one ordered current-project inventory marker pair.");
        }

        return document[(begin + InventoryBegin.Length)..end];
    }

    private static string RemoveProjectRow(string document, string project)
    {
        var pattern = $@"^\|\s*`{Regex.Escape(project)}`\s*\|.*(?:\r?\n|$)";
        return Regex.Replace(document, pattern, string.Empty, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
    }

    private static string AddProjectRow(string document, string project)
    {
        var row = $"| `{project}` | Invented reference or responsibility. | Invented responsibility. | Invented exclusion. |{Environment.NewLine}";
        return document.Replace(InventoryEnd, row + InventoryEnd, StringComparison.Ordinal);
    }

    private static string DuplicateProjectRow(string document, string project)
    {
        var pattern = $@"^\|\s*`{Regex.Escape(project)}`\s*\|.*(?:\r?\n|$)";
        var match = Regex.Match(
            document,
            pattern,
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(1));
        return match.Success
            ? document.Insert(match.Index + match.Length, match.Value)
            : document;
    }

    private static GraphMutation RemoveProductionReference(string root, string architecture)
    {
        var production = DiscoverProjects(root)
            .Where(project => project.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var project in production.Order(StringComparer.OrdinalIgnoreCase))
        {
            var projectFile = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            var reference = XDocument.Load(projectFile, LoadOptions.None)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Normalize(Path.GetRelativePath(
                    root,
                    Path.GetFullPath(Path.Combine(projectDirectory, include!)))))
                .Where(production.Contains)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (reference is null)
                continue;

            var pattern = $@"^\|\s*`{Regex.Escape(project)}`\s*\|.*$";
            var match = Regex.Match(
                architecture,
                pattern,
                RegexOptions.Multiline,
                TimeSpan.FromSeconds(1));
            if (!match.Success)
                continue;

            var mutatedRow = match.Value.Replace($"`{reference}`", "none", StringComparison.Ordinal);
            if (mutatedRow == match.Value)
                continue;

            var mutated = architecture.Remove(match.Index, match.Length)
                .Insert(match.Index, mutatedRow);
            return new GraphMutation(mutated, project, reference);
        }

        throw new InvalidOperationException("Could not find a documented production reference to mutate.");
    }

    private static Documentation ReadDocuments(string root) => new(
        File.ReadAllText(Path.Combine(root, ReadmePath)),
        File.ReadAllText(Path.Combine(root, ArchitecturePath)));

    private static bool IsGeneratedPath(string path) =>
        Normalize(path).Split('/').Any(segment =>
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

    private static void RequireContains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record Documentation(string Readme, string Architecture);
    private sealed record GraphMutation(string Architecture, string Project, string Reference);
    private sealed record InventoryRow(string Project, IReadOnlyList<string> DetailColumns);
}
