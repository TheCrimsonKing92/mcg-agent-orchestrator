using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.ProjectFactHelpers;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Discovers a module from declarations; commands are measured only by a supplied measurer.</summary>
public sealed class GoProjectDiscoveryAdapter : IProjectDiscoveryAdapter
{
    private const string GoExecutablePackage = "main";

    // Go's go/build knownOS and knownArch filename suffix vocabulary, including historical targets.
    private static readonly HashSet<string> OperatingSystems = new(StringComparer.Ordinal)
    {
        "aix", "android", "darwin", "dragonfly", "freebsd", "hurd", "illumos", "ios", "js", "linux",
        "nacl", "netbsd", "openbsd", "plan9", "solaris", "wasip1", "windows", "zos"
    };
    private static readonly HashSet<string> Architectures = new(StringComparer.Ordinal)
    {
        "386", "amd64", "amd64p32", "arm", "armbe", "arm64", "arm64be", "loong64", "mips", "mipsle",
        "mips64", "mips64le", "mips64p32", "mips64p32le", "ppc", "ppc64", "ppc64le", "riscv", "riscv64",
        "s390", "s390x", "sparc", "sparc64", "wasm"
    };
    private sealed record FileDeclaration(string Path, bool IsTest, GoSourceFileReader.SourceFile Source);

    public ProjectModel Discover(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null,
        IUnitCommandMeasurer? measurer = null, UnitCommandKinds measuredKinds = UnitCommandKinds.All)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        var modulePath = Path.Combine(root, "go.mod");
        if (!IsSafePath(root, modulePath))
            throw new InvalidOperationException("Cannot discover a module through a reparse point: go.mod.");
        var module = GoModReader.Read(modulePath);
        if (module.Path is null)
            throw new InvalidOperationException("go.mod declares no module directive.");

        var questions = new List<ProjectOwnerQuestion>();
        var tracked = DiscoveryTrackedPaths.Read(root);
        tracked.AskIfDegraded(questions);
        var exclusions = new HashSet<string>(excludedDirectoryNames ?? [], StringComparer.OrdinalIgnoreCase);
        exclusions.UnionWith(["bin", "obj", ".git", "testdata", "vendor"]);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<FileDeclaration>();
        Visit(root);

        var groups = files.GroupBy(file => DirectoryId(file.Path))
            .Where(group => group.Any(file => !file.IsTest))
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var unitIds = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var units = new List<ProjectUnit>();
        var dependencies = new List<UnitDependency>();
        var setups = new List<UnitTestSetup>();
        var commands = new List<UnitCommands>();
        var hazards = new List<SharedStateHazard>();
        foreach (var group in groups)
        {
            var id = group.Key;
            var ordered = group.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            var first = ordered.First(file => !file.IsTest);
            var test = ordered.FirstOrDefault(file => file.IsTest);
            var source = Source(first, first.Source.Package!.Line);
            var testSource = test is null ? source : Source(test, test.Source.Package!.Line);
            var importPath = module.Path.Value + (id == "." ? "" : "/" + id);
            units.Add(new(id, High(importPath, new FactSource("go.mod", module.Path.Line)),
                High(id, source), High<bool?>(test is not null, testSource)));
            var target = id == "." ? "." : "./" + id;
            commands.Add(new(id, High(first.Source.Package.Value == GoExecutablePackage
                    ? $"go build -o NUL {target}" : $"go build {target}", source),
                test is null ? null : High($"go test {target}", testSource)));
            if (test is not null)
                setups.Add(new(id, High("go test", testSource), High("go", testSource)));

            foreach (var file in ordered)
            {
                foreach (var import in file.Source.Imports)
                {
                    var to = import.Path == module.Path.Value ? "."
                        : import.Path.StartsWith(module.Path.Value + "/", StringComparison.Ordinal)
                            ? import.Path[(module.Path.Value.Length + 1)..] : null;
                    if (to is null || to == id) continue;
                    var isSkipped = skipped.Any(path => to == path || to.StartsWith(path + "/", StringComparison.Ordinal));
                    if (!unitIds.Contains(to) && !isSkipped) continue;
                    var edgeSource = Source(file, import.Line);
                    dependencies.Add(new(id, to, edgeSource, isSkipped ? FactConfidence.Low : FactConfidence.High));
                    if (isSkipped)
                        Ask($"dependencies/{id}/{to}", "Confirm this import into a directory excluded from discovery.", edgeSource, questions);
                }
                foreach (var line in file.Source.BuildCalls)
                    Hazard("go-test-builds-binary", id, line);
                foreach (var import in file.Source.Imports.Where(import => import.Path == "C"))
                    Hazard("go-cgo", "cgo", import.Line);
                foreach (var constraint in file.Source.Constraints)
                    Hazard("go-build-constraint", constraint.Value, constraint.Line);
                if (FilenameConstraint(file.Path) is { } suffix)
                    Hazard("go-build-constraint", "filename:_" + suffix, file.Source.Package!.Line);

                void Hazard(string name, string key, int line) =>
                    hazards.Add(new(id, name, High(key, Source(file, line))));
            }
        }

        var environment = new List<EnvironmentNeed>
        {
            new("go-toolchain", module.Go is { } go ? High(go.Value, new FactSource("go.mod", go.Line))
                : Fact("undetermined", FactConfidence.Low, new FactSource("go.mod", 1),
                    "environment/go-toolchain", "Which Go version is required? go.mod has no go directive.", questions))
        };
        if (module.Toolchain is { } pin)
            environment.Add(new("go-toolchain-pin", High(pin.Value, new FactSource("go.mod", pin.Line))));
        var measurements = UnitMeasurementCollector.Collect(root, commands, measurer, measuredKinds, questions);
        var learned = IntegrationBranchReader.Read(root);
        if (learned.Branch is null)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey, $"What is the integration branch? {learned.Reason}",
                new FactSource(".git", 1), questions);
        else if (learned.Branch.Confidence != FactConfidence.High)
            Ask(LearnedIntegrationBranch.OwnerQuestionKey,
                $"Confirm that '{learned.Branch.Value}' is the integration branch learned from the checked-out branch hint.",
                learned.Branch.Source, questions);

        return new(ProjectModel.CurrentSchemaVersion, ".", units,
            dependencies.GroupBy(edge => (edge.FromUnit, edge.ToUnit))
                .Select(group => group.OrderByDescending(edge => edge.Confidence).First())
                .OrderBy(edge => edge.FromUnit, StringComparer.Ordinal).ThenBy(edge => edge.ToUnit, StringComparer.Ordinal).ToArray(),
            setups, questions.DistinctBy(question => question.FactKey)
                .OrderBy(question => question.FactKey, StringComparer.Ordinal).ToArray(), commands, environment, measurements,
            hazards.DistinctBy(hazard => (hazard.UnitId, hazard.Name, hazard.IsolationKey.Value))
                .OrderBy(hazard => hazard.UnitId, StringComparer.Ordinal).ThenBy(hazard => hazard.Name, StringComparer.Ordinal)
                .ThenBy(hazard => hazard.IsolationKey.Value, StringComparer.Ordinal).ToArray(), learned.Branch, ProjectStack.Go);

        void Visit(string directory)
        {
            var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            try
            {
                if (directory != root)
                {
                    var name = Path.GetFileName(directory);
                    if (!tracked.IncludesDirectory(directory) || exclusions.Contains(name) || name.StartsWith('.') ||
                        name.StartsWith('_') || !IsSafePath(root, directory))
                    { skipped.Add(relative); return; }
                    if (File.Exists(Path.Combine(directory, "go.mod")))
                    {
                        skipped.Add(relative);
                        Ask($"units/{relative}/module", "This directory is a separate Go module; confirm its discovery scope.",
                            new FactSource(relative + "/go.mod", 1), questions);
                        return;
                    }
                }
                foreach (var path in Directory.GetFiles(directory, "*.go").Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(path);
                    if (!tracked.IncludesFile(path) || !name.EndsWith(".go", StringComparison.Ordinal) ||
                        name.StartsWith('.') || name.StartsWith('_') || !IsSafePath(root, path)) continue;
                    var filePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                    try
                    {
                        var isTest = name.EndsWith("_test.go", StringComparison.Ordinal);
                        var parsed = GoSourceFileReader.Read(File.ReadAllText(path), isTest);
                        if (parsed.Package is null)
                            Ask($"discovery/files/{filePath}/package", "This Go file has no package clause.", new FactSource(filePath, 1), questions);
                        else files.Add(new(filePath, isTest, parsed));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Ask($"discovery/files/{filePath}/read", "Discovery could not read this Go source file.", new FactSource(filePath, 1), questions);
                    }
                }
                foreach (var child in Directory.GetDirectories(directory).Order(StringComparer.Ordinal)) Visit(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Ask($"discovery/directories/{relative}/membership", "Discovery could not inspect this directory; package membership may be incomplete.",
                    new FactSource(relative == "." ? "go.mod" : relative, 1), questions);
            }
        }
    }

    private static ProjectFact<T> High<T>(T value, FactSource source) => new(value, source, FactConfidence.High);
    private static FactSource Source(FileDeclaration file, int line) => new(file.Path, line);
    private static string DirectoryId(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";

    private static string? FilenameConstraint(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith("_test", StringComparison.Ordinal)) name = name[..^5];
        var parts = name.Split('_');
        if (parts.Length < 2) return null;
        if (parts.Length >= 3 && OperatingSystems.Contains(parts[^2]) && Architectures.Contains(parts[^1]))
            return parts[^2] + "_" + parts[^1];
        return OperatingSystems.Contains(parts[^1]) || Architectures.Contains(parts[^1]) ? parts[^1] : null;
    }
}
