using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads declarations; executes learned commands only with an explicitly supplied measurer.</summary>
public sealed class DotnetProjectDiscoveryAdapter : IProjectDiscoveryAdapter
{
    private const string Undetermined = "undetermined";
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly IReadOnlyList<string> RunnerProperties = DotnetTestMarkers.RunnerProperties;

    public ProjectModel Discover(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null,
        IUnitCommandMeasurer? measurer = null, UnitCommandKinds measuredKinds = UnitCommandKinds.All)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");

        var questions = new List<ProjectOwnerQuestion>();
        var exclusions = new HashSet<string>(excludedDirectoryNames ?? [], StringComparer.OrdinalIgnoreCase);
        exclusions.UnionWith(["bin", "obj", ".git"]);
        var files = EnumerateFiles(root, exclusions).Order(StringComparer.Ordinal).ToArray();
        var solutions = files.Where(file => Path.GetDirectoryName(file) == root &&
            Path.GetExtension(file).ToLowerInvariant() is ".sln" or ".slnx").ToArray();
        var listed = new Dictionary<string, (string Name, FactSource Source)>(PathComparer);
        foreach (var solution in solutions)
        {
            foreach (var entry in ReadSolution(root, solution, questions))
                listed.TryAdd(entry.Path, (entry.Name, entry.Source));
        }

        var projects = files.Where(IsProjectFile).Concat(listed.Keys.Select(path => Path.GetFullPath(Path.Combine(root, path))))
            .Distinct(PathComparer).OrderBy(path => Relative(root, path), StringComparer.Ordinal).ToArray();
        var projectPaths = projects.ToHashSet(PathComparer);
        var units = new List<ProjectUnit>();
        var dependencies = new List<UnitDependency>();
        var setups = new List<UnitTestSetup>();
        var commands = new List<UnitCommands>();
        var globalRunner = ReadGlobalRunner(root, questions);
        foreach (var project in projects)
        {
            var id = Relative(root, project);
            var declaration = listed.GetValueOrDefault(id);
            var source = declaration.Source ?? new FactSource(id, 1);
            var confidence = declaration.Source is null ? FactConfidence.Low
                : solutions.Length > 1 ? FactConfidence.Medium : FactConfidence.High;
            var document = ReadXml(root, project, out var error);
            if (document?.Root?.Name.LocalName != "Project")
            {
                document = null;
                error ??= "The file has no Project root element.";
            }
            if (document is null)
            {
                confidence = FactConfidence.Low;
                if (File.Exists(project))
                    source = new FactSource(id, 1);
            }

            var reason = error ?? (declaration.Source is null
                ? "No solution establishes this unit's membership."
                : "Several solutions exist; confirm the canonical solution.");
            var location = Fact(id, confidence, source, $"units/{id}/location", reason, questions);
            var name = Fact(declaration.Name ?? Path.GetFileNameWithoutExtension(project), confidence,
                source, $"units/{id}/name", reason, questions);
            var properties = document?.Descendants().Where(element =>
                element.Parent?.Name.LocalName == "PropertyGroup")
                .Select(element => Evidence(root, project, element, element.Name.LocalName, element.Value.Trim()))
                .ToArray() ?? [];
            var packages = document?.Descendants().Where(element => element.Name.LocalName == "PackageReference" &&
                element.Attribute("Include") is not null)
                .Select(element => Evidence(root, project, element, "package", element.Attribute("Include")!.Value))
                .ToList() ?? [];
            if (document?.Root?.Attribute("Sdk") is { } sdk && sdk.Value.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase))
                packages.Add(Evidence(root, project, document.Root, "package", "MSTest.TestFramework"));

            var testStatus = document is not null && DotnetTestMarkers.IsMarkerFree(document)
                ? new ProjectFact<bool?>(false, new FactSource(id, 1), FactConfidence.High)
                : DiscoverTestStatus(id, properties, packages, source, questions);
            units.Add(new ProjectUnit(id, name, location, testStatus));
            if (testStatus.Value != false)
            {
                setups.Add(new UnitTestSetup(id,
                    DiscoverFramework(id, packages, source, questions),
                    DiscoverRunner(id, properties, packages, globalRunner, source, questions)));
            }

            commands.Add(DotnetUnitCommandDeriver.Derive(id, document, testStatus,
                setups.LastOrDefault(setup => setup.UnitId == id)?.Runner, questions));

            foreach (var reference in document?.Descendants().Where(element =>
                element.Name.LocalName == "ProjectReference") ?? [])
            {
                var include = reference.Attribute("Include")?.Value ?? Undetermined;
                var referenceSource = Source(root, project, reference);
                var resolved = ResolveProjectPath(root, Path.GetDirectoryName(project)!, include);
                var target = resolved is null ? include.Replace('\\', '/') : Relative(root, resolved);
                var reliable = resolved is not null && projectPaths.Contains(resolved) && File.Exists(resolved) && IsProjectFile(resolved) &&
                    IsReliable(reference, include);
                var edgeConfidence = reliable ? FactConfidence.High : FactConfidence.Low;
                dependencies.Add(new UnitDependency(id, target, referenceSource, edgeConfidence));
                if (!reliable)
                    Ask($"dependencies/{id}/{target}", "Confirm this conditional, missing, or unresolved project reference.", referenceSource, questions);
            }
        }

        var environment = DotnetEnvironmentNeedReader.Read(root, commands, questions);
        var measurements = UnitMeasurementCollector.Collect(root, commands, measurer, measuredKinds, questions);
        // Paths in the snapshot are relative to its logical root, independent of the discovery host.
        return new ProjectModel(ProjectModel.CurrentSchemaVersion, ".", units,
            dependencies.GroupBy(edge => (edge.FromUnit, edge.ToUnit))
                // Preserve the weakest declaration and its owner question rather than hiding uncertainty.
                .Select(group => group.OrderByDescending(edge => edge.Confidence).First())
                .OrderBy(edge => edge.FromUnit, StringComparer.Ordinal).ThenBy(edge => edge.ToUnit, StringComparer.Ordinal).ToArray(),
            setups, questions.DistinctBy(question => question.FactKey)
                .OrderBy(question => question.FactKey, StringComparer.Ordinal).ToArray(), commands, environment, measurements);
    }

    private static ProjectFact<bool?> DiscoverTestStatus(string id, Declaration[] properties,
        List<Declaration> packages, FactSource fallback, List<ProjectOwnerQuestion> questions)
    {
        var status = properties.Where(property => property.Name == "IsTestProject").ToArray();
        var testPackages = packages.Where(package => FrameworkName(package.Value) is not null ||
            DotnetTestMarkers.IsTestSdkPackage(package.Value)).ToArray();
        if (status.Length == 1 && status[0].Reliable && bool.TryParse(status[0].Value, out var isTest) &&
            (isTest || testPackages.Length == 0))
            return new ProjectFact<bool?>(isTest, status[0].Source, FactConfidence.High);
        if (status.Length == 0 && testPackages.Any(package => package.Reliable))
            return new ProjectFact<bool?>(true, testPackages.First(package => package.Reliable).Source, FactConfidence.High);

        return Fact<bool?>(null, FactConfidence.Low, status.FirstOrDefault()?.Source ?? testPackages.FirstOrDefault()?.Source ?? fallback,
            $"units/{id}/isTest", "Is this unit a test unit? Its declarations do not establish an unconditional test status.", questions);
    }

    private static ProjectFact<string> DiscoverFramework(string id, List<Declaration> packages,
        FactSource fallback, List<ProjectOwnerQuestion> questions)
    {
        var candidates = packages.Where(package => FrameworkName(package.Value) is not null).ToArray();
        var names = candidates.Select(package => FrameworkName(package.Value)).Distinct().ToArray();
        if (names.Length == 1 && candidates.All(package => package.Reliable))
            return new ProjectFact<string>(names[0]!, candidates[0].Source, FactConfidence.High);

        return Fact(Undetermined, FactConfidence.Low, candidates.FirstOrDefault()?.Source ?? fallback,
            $"tests/{id}/framework", "Which test framework does this unit use? No single unconditional framework declaration was found.", questions);
    }

    private static ProjectFact<string> DiscoverRunner(string id, Declaration[] properties,
        List<Declaration> packages, Declaration? globalRunner, FactSource fallback, List<ProjectOwnerQuestion> questions)
    {
        var runnerFlags = properties.Where(property => RunnerProperties.Contains(property.Name, StringComparer.Ordinal)).ToArray();
        var mtpPackages = packages.Where(package => package.Value.StartsWith("xunit.v3.mtp-", StringComparison.OrdinalIgnoreCase) ||
            package.Value.StartsWith("xunit.v3.core.mtp-", StringComparison.OrdinalIgnoreCase)).ToArray();
        var sdk = packages.FirstOrDefault(package => DotnetTestMarkers.IsTestSdkPackage(package.Value));
        var vstestAdapter = packages.FirstOrDefault(package => DotnetTestMarkers.IsVstestAdapter(package.Value));
        string? runner = null;
        var source = runnerFlags.FirstOrDefault()?.Source ?? mtpPackages.FirstOrDefault()?.Source ?? sdk?.Source ?? fallback;
        var flagsReliable = runnerFlags.All(flag => flag.Reliable && bool.TryParse(flag.Value, out _));
        var enabled = runnerFlags.Where(flag => flag.Value.Equals("true", StringComparison.OrdinalIgnoreCase)).ToArray();
        var disabled = runnerFlags.Where(flag => flag.Value.Equals("false", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (flagsReliable && enabled.Length > 0 && disabled.Length == 0)
        {
            runner = "MTP";
            source = enabled[0].Source;
        }
        else if (flagsReliable && enabled.Length == 0 && mtpPackages.Length > 0 &&
            mtpPackages.All(package => package.Reliable) && sdk is null && vstestAdapter is null && disabled.Length == 0)
        {
            runner = "MTP";
            source = mtpPackages[0].Source;
        }
        else if (flagsReliable && enabled.Length == 0 && sdk is { Reliable: true } && vstestAdapter is { Reliable: true })
        {
            runner = "VSTest";
            source = sdk.Source;
        }

        if (runner is not null && globalRunner is not null &&
            !globalRunner.Value.Equals(runner == "MTP" ? "Microsoft.Testing.Platform" : "VSTest", StringComparison.OrdinalIgnoreCase))
        {
            runner = null;
            source = globalRunner.Source;
        }
        return runner is not null ? new ProjectFact<string>(runner, source, FactConfidence.High)
            : Fact(Undetermined, FactConfidence.Low, source, $"tests/{id}/runner",
                "Which test runner does this unit use? Declarations are missing, conditional, or conflicting; confirm the runner.", questions);
    }

    private static string? FrameworkName(string package) => DotnetTestMarkers.FrameworkName(package);

    private static IEnumerable<(string Path, string Name, FactSource Source)> ReadSolution(
        string root, string solution, List<ProjectOwnerQuestion> questions)
    {
        var entries = new List<(string Path, string Name, FactSource Source)>();
        if (Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var document = ReadXml(root, solution, out var error);
            if (document?.Root?.Name.LocalName != "Solution")
            {
                Ask($"solutions/{Relative(root, solution)}", $"Confirm solution membership: {error ?? "Invalid Solution root element."}",
                    new FactSource(Relative(root, solution), 1), questions);
                return entries;
            }
            foreach (var project in document.Descendants().Where(element => element.Name.LocalName == "Project"))
                Add(project.Attribute("Path")?.Value ?? "", project.Attribute("Name")?.Value, Source(root, solution, project));
        }
        else
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(solution);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Ask($"solutions/{Relative(root, solution)}", $"Confirm solution membership: {exception.Message}",
                    new FactSource(Relative(root, solution), 1), questions);
                return entries;
            }
            for (var index = 0; index < lines.Length; index++)
            {
                var match = Regex.Match(lines[index], "^\\s*Project\\(\"[^\"]+\"\\)\\s*=\\s*\"([^\"]+)\",\\s*\"([^\"]+)\"");
                if (match.Success)
                    Add(match.Groups[2].Value, match.Groups[1].Value, new FactSource(Relative(root, solution), index + 1));
            }
        }
        return entries;

        void Add(string path, string? name, FactSource source)
        {
            if (!IsProjectFile(path))
            {
                if (Path.GetExtension(path).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                    Ask($"solutions/{Relative(root, solution)}/{path}",
                        "This project type is unsupported by this discovery adapter; confirm its unit and dependencies.", source, questions);
                return;
            }
            var resolved = ResolveProjectPath(root, Path.GetDirectoryName(solution)!, path);
            if (resolved is null)
                Ask($"solutions/{Relative(root, solution)}/{path}", "Confirm this unresolved solution project path; it was not read.", source, questions);
            else
                entries.Add((Relative(root, resolved), name ?? Path.GetFileNameWithoutExtension(resolved), source));
        }
    }

    private static Declaration? ReadGlobalRunner(string root, List<ProjectOwnerQuestion> questions)
    {
        var path = Path.Combine(root, "global.json");
        if (!File.Exists(path) || !IsSafePath(root, path))
            return null;
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object &&
                test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String)
            {
                var line = Array.FindIndex(json.Split('\n'), text => text.Contains("\"runner\"", StringComparison.Ordinal)) + 1;
                return new Declaration("runner", runner.GetString()!, new FactSource("global.json", Math.Max(1, line)), true);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Ask("configuration/globalRunner", $"Confirm the test runner; global.json could not be read: {exception.Message}", new FactSource("global.json", 1), questions);
            return new Declaration("runner", Undetermined, new FactSource("global.json", 1), false);
        }
        return null;
    }

    private static XDocument? ReadXml(string root, string path, out string? error)
    {
        error = null;
        if (!IsSafePath(root, path))
        {
            error = "The path is a link or is outside the repository.";
            return null;
        }
        try
        {
            return XDocument.Load(path, LoadOptions.SetLineInfo);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, HashSet<string> exclusions)
    {
        foreach (var file in Directory.EnumerateFiles(root))
            if ((IsProjectFile(file) || Path.GetExtension(file).ToLowerInvariant() is ".sln" or ".slnx") &&
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                yield return file;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (exclusions.Contains(Path.GetFileName(directory)) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            foreach (var file in EnumerateFiles(directory, exclusions))
                yield return file;
        }
    }

    private static string? ResolveProjectPath(string root, string directory, string include)
    {
        if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal) ||
            include.Contains('@') || include.IndexOfAny(['*', '?', ';']) >= 0)
            return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
            return IsSafePath(root, path) ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static bool IsSafePath(string root, string path)
    {
        var relative = Relative(root, path);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false;
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static bool IsProjectFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static FactSource Source(string root, string path, XElement element) =>
        new(Relative(root, path), Math.Max(1, ((IXmlLineInfo)element).LineNumber));
    private static Declaration Evidence(string root, string path, XElement element, string name, string value) =>
        new(name, value, Source(root, path, element), IsReliable(element, value));
    private static bool IsReliable(XElement element, string value) =>
        !value.Contains("$(", StringComparison.Ordinal) && !value.Contains("@(", StringComparison.Ordinal) &&
        !element.AncestorsAndSelf().Any(ancestor => ancestor.Attribute("Condition") is not null ||
            ancestor.Name.LocalName is "Choose" or "When" or "Otherwise" or "Target");

    internal static ProjectFact<T> Fact<T>(T value, FactConfidence confidence, FactSource source,
        string key, string reason, List<ProjectOwnerQuestion> questions)
    {
        if (confidence != FactConfidence.High)
            Ask(key, reason, source, questions);
        return new ProjectFact<T>(value, source, confidence);
    }

    internal static void Ask(string key, string reason, FactSource source, List<ProjectOwnerQuestion> questions) =>
        questions.Add(new ProjectOwnerQuestion(key, $"{key}: {reason}", source));

    private sealed record Declaration(string Name, string Value, FactSource Source, bool Reliable);
}
