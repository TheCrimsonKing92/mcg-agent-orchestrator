using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public class CanaryEngineSurfaceCoverageTests
{
    private const string CollaboratorSurfaceName = "acceptance-collaborators";
    private const string SettingsPrefix =
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGateEngineSettings";

    // Parallel-safe: read-only assembly/source inputs, each cached once; no shared mutations or writes.
    private static readonly Lazy<Architecture> InfrastructureArchitecture = new(
        () => new ArchLoader().LoadAssemblies(typeof(GoalAcceptanceVerifier).Assembly).Build(),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> DeclarationIndex = new(
        BuildDeclarationIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Verifier_dependencies_are_covered_by_canary_surface()
    {
        var uncovered = UncoveredCollaboratorSourcePaths(
            path => PostLandingCanaryTrigger.Evaluate([path]).ShouldRun);

        Assert.True(uncovered.Length == 0,
            $"{uncovered.Length} uncovered acceptance collaborator source files. " +
            "Extend AcceptanceEngineSurfaceRegistry.Surfaces:\n" + string.Join("\n", uncovered));
    }

    [Fact]
    public void Missing_settings_prefix_reports_only_settings_source()
    {
        var prefixes = PostLandingCanaryTrigger.EnginePathPrefixes;
        Assert.Single(prefixes.Where(prefix => prefix == SettingsPrefix));

        var uncovered = UncoveredCollaboratorSourcePaths(
            CoveragePredicate(prefixes.Where(prefix => prefix != SettingsPrefix)));

        Assert.Equal([SettingsPrefix + ".cs"], uncovered);
    }

    [Fact]
    public void Collaborator_surface_exactly_matches_preexisting_coverage_gap()
    {
        var surface = Assert.Single(AcceptanceEngineSurfaceRegistry.Surfaces,
            surface => surface.Name == CollaboratorSurfaceName);
        var previousPrefixes = AcceptanceEngineSurfaceRegistry.Surfaces
            .Where(surface => surface.Name != CollaboratorSurfaceName)
            .SelectMany(surface => surface.PathPrefixes);
        var uncovered = UncoveredCollaboratorSourcePaths(CoveragePredicate(previousPrefixes));
        // Sort after stripping the extension: dotted partial files sort differently from their main file.
        var expectedPrefixes = uncovered.Select(path => path[..^3]).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(expectedPrefixes);
        Assert.Equal(expectedPrefixes, surface.PathPrefixes);
    }

    private static string[] UncoveredCollaboratorSourcePaths(Func<string, bool> isCovered)
    {
        var assemblyName = typeof(GoalAcceptanceVerifier).Assembly.FullName;
        var verifierName = typeof(GoalAcceptanceVerifier).FullName!;
        bool IsVerifierType(IType type) => type.FullName == verifierName ||
            type.FullName.StartsWith(verifierName + "+", StringComparison.Ordinal) ||
            type.FullName.StartsWith(verifierName + "/", StringComparison.Ordinal);

        // ArchUnitNET folds generated method bodies (closures/state machines) into declaring-type
        // dependencies; also include every nested type exposed by its model as a direct root.
        var roots = InfrastructureArchitecture.Value.Types
            .Where(type => type.Assembly.FullName == assemblyName && IsVerifierType(type)).ToArray();
        Assert.Contains(roots, type => type.FullName == verifierName);

        var dependencies = roots.SelectMany(type => type.Dependencies)
            .Select(dependency => dependency.Target)
            // Targets without an assembly are not declared in the Infrastructure assembly.
            .Where(type => type.Assembly?.FullName == assemblyName && !IsVerifierType(type))
            .ToArray();
        Assert.NotEmpty(dependencies);

        var declarations = DeclarationIndex.Value;
        var sourcePaths = dependencies.Select(type => SimpleName(type.FullName))
            .Distinct(StringComparer.Ordinal)
            .Where(declarations.ContainsKey)
            .SelectMany(name => declarations[name])
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(sourcePaths);

        return sourcePaths.Where(path => !isCovered(path)).Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, string[]> BuildDeclarationIndex()
    {
        var root = VerifiedRepositoryRoot.Find();
        var folder = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", "Workspaces");
        Assert.True(Directory.Exists(folder), $"Missing acceptance collaborator source folder: {folder}");
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            // BaseTypeDeclarationSyntax includes classes, records, structs, interfaces and enums.
            foreach (var declaration in syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var name = declaration.Identifier.ValueText;
                if (!index.TryGetValue(name, out var paths))
                    index[name] = paths = new HashSet<string>(StringComparer.Ordinal);
                paths.Add(path);
            }
        }

        Assert.NotEmpty(index);
        return index.ToDictionary(pair => pair.Key,
            pair => pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    private static string SimpleName(string fullName) => fullName.Split('.', '+', '/')[^1].Split('`')[0];

    private static Func<string, bool> CoveragePredicate(IEnumerable<string> pathPrefixes)
    {
        var prefixes = pathPrefixes.Select(Normalize).Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return sourcePath =>
        {
            var path = Normalize(sourcePath);
            return path.Length > 0 && prefixes.Any(prefix =>
                path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix.EndsWith('/') ? prefix : prefix + "/", StringComparison.OrdinalIgnoreCase) ||
                (!Path.HasExtension(prefix) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        };
    }

    private static string Normalize(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
}
