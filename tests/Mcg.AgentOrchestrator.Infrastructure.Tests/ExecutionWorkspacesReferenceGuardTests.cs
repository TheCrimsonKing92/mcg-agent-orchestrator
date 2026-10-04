using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

// Parallel-safe: repository source reads only; no shared state is changed.
public sealed class ExecutionWorkspacesReferenceGuardTests
{
    private const string InfrastructureSourceRoot = "src/Mcg.AgentOrchestrator.Infrastructure/";
    private static readonly string[] ExecutionFolders = ["Processes", "Workers", "Persistence", "Verification"];
    private static readonly Lazy<Architecture> InfrastructureArchitecture = new(
        () => new ArchLoader().LoadAssemblies(typeof(CohortAcceptanceStore).Assembly).Build(),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> DeclarationIndex = new(
        BuildDeclarationIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    // These assembly-scope compiler-injected types have no source declaration or containing source type.
    // Keep this list closed: any other unmatched target must be reported, never silently dropped.
    private static readonly HashSet<string> AssemblyInjectedTypes = new(StringComparer.Ordinal)
    {
        "<PrivateImplementationDetails>",
        "Microsoft.CodeAnalysis.EmbeddedAttribute",
        "System.Runtime.CompilerServices.NullableAttribute",
        "System.Runtime.CompilerServices.NullableContextAttribute",
        "System.Runtime.CompilerServices.RefSafetyRulesAttribute"
    };

    [Fact]
    public void ExecutionTypes_DependOnlyOnExecutionDeclarations()
    {
        var assemblyName = typeof(CohortAcceptanceStore).Assembly.FullName;
        var types = InfrastructureArchitecture.Value.Types
            .Where(type => type.Assembly.FullName == assemblyName).ToArray();
        var declarations = DeclarationIndex.Value;
        Assert.NotEmpty(types);
        Assert.Contains(types, type => type.FullName == typeof(CohortAcceptanceStore).FullName);
        // Generic parameters are variables, not independently declared types. Keep their constraint
        // dependencies attributed to the owning type, including parameters declared by its methods.
        var pairs = types.SelectMany(type => type.Dependencies
                .Concat(type.GenericParameters.Concat(type.Members.SelectMany(member => member.GenericParameters))
                    .SelectMany(parameter => parameter.Dependencies))
                .Where(dependency => !dependency.Target.IsGenericParameter &&
                    dependency.Target.Assembly?.FullName == assemblyName)
                .Select(dependency => (Source: type.FullName, Target: dependency.Target.FullName)))
            .ToArray();
        Assert.NotEmpty(pairs);
        Assert.Contains(pairs, pair => pair.Source == typeof(CohortAcceptanceStore).FullName &&
            pair.Target == typeof(AcceptanceCohortMaterializationFailureKind).FullName);

        var violations = EvaluateBoundary(declarations, pairs);

        Assert.True(violations.Length == 0,
            "Execution types must depend only on Execution declarations in Infrastructure:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void ExecutionBoundary_ReportsKnownViolatingPair()
    {
        var source = typeof(CohortAcceptanceStore).FullName!;
        var target = typeof(GoalWorktrees).FullName!;
        var declarations = DeclarationIndex.Value;
        var expectedFiles = declarations[target].Where(path => !IsExecutionFile(path));

        var violations = EvaluateBoundary(declarations, [(source, target)]);

        Assert.Equal($"{source} -> {target} ({string.Join(", ", expectedFiles)})",
            Assert.Single(violations));
    }

    private static IReadOnlyDictionary<string, string[]> BuildDeclarationIndex()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var folder = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure");
        Assert.True(Directory.Exists(folder), $"Missing Infrastructure source directory: {folder}");
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (path.Split('/').Any(segment => segment is "bin" or "obj" or "artifacts"))
                continue;
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            foreach (var declaration in syntax.DescendantNodes()
                .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                // Namespace and containing-type ancestry is part of identity, including nested declarations.
                var parts = declaration.AncestorsAndSelf().Reverse().Select(node => node switch
                {
                    BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                    BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                    DelegateDeclarationSyntax type => type.Identifier.ValueText,
                    _ => null
                }).Where(part => part is not null);
                var name = CanonicalTypeName(string.Join(".", parts));
                if (!index.TryGetValue(name, out var paths))
                    index[name] = paths = new HashSet<string>(StringComparer.Ordinal);
                paths.Add(path);
            }
        }

        Assert.NotEmpty(index);
        return index.ToDictionary(pair => pair.Key,
            pair => pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    private static string[] EvaluateBoundary(IReadOnlyDictionary<string, string[]> declarations,
        IEnumerable<(string Source, string Target)> pairs)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (source, target) in pairs)
        {
            var sourceName = ResolveDeclaration(source, declarations);
            if (sourceName is null || !declarations[sourceName].All(IsExecutionFile))
                continue;
            var targetName = ResolveDeclaration(target, declarations);
            // Anonymous types are emitted at assembly scope; their use belongs to the outermost
            // declared source containing the method that creates/consumes them, not to a new folder.
            if (targetName is null && CanonicalTypeName(target).StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
                targetName = OutermostDeclaration(sourceName, declarations);
            if (targetName is null)
            {
                var canonicalTarget = CanonicalTypeName(target);
                if (!AssemblyInjectedTypes.Contains(canonicalTarget) &&
                    !canonicalTarget.StartsWith("<PrivateImplementationDetails>.", StringComparison.Ordinal))
                    violations.Add($"{sourceName} -> {target} (no declaration)");
                continue;
            }

            var outsideFiles = declarations[targetName].Where(path => !IsExecutionFile(path)).ToArray();
            if (outsideFiles.Length > 0)
                violations.Add($"{sourceName} -> {targetName} ({string.Join(", ", outsideFiles)})");
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsExecutionFile(string path) => ExecutionFolders.Any(folder =>
        path.StartsWith(InfrastructureSourceRoot + folder + "/", StringComparison.Ordinal));

    private static string? ResolveDeclaration(string fullName, IReadOnlyDictionary<string, string[]> declarations)
    {
        var name = CanonicalTypeName(fullName);
        if (declarations.ContainsKey(name))
            return name;
        // Generated closures/state machines contain '<'; use the outermost declared containing type.
        // Never fall back to a simple name or attribute an ordinary unresolved nested type to its parent.
        var generatedSegment = name.IndexOf(".<", StringComparison.Ordinal);
        return generatedSegment >= 0 ? OutermostDeclaration(name[..generatedSegment], declarations) : null;
    }

    private static string? OutermostDeclaration(string name, IReadOnlyDictionary<string, string[]> declarations)
    {
        for (var end = name.IndexOf('.'); end >= 0; end = name.IndexOf('.', end + 1))
            if (declarations.ContainsKey(name[..end]))
                return name[..end];
        return declarations.ContainsKey(name) ? name : null;
    }

    private static string CanonicalTypeName(string name) =>
        Regex.Replace(name.Replace('+', '.').Replace('/', '.'), @"`\d+", "");

    [Fact]
    public void ProcessesAndWorkersSources_DoNotReferenceBuildEnvironmentManagerOrAcceptanceVerifier()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var area in new[] { "Processes", "Workers" })
        {
            var directory = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", area);
            Assert.True(Directory.Exists(directory), $"Missing source directory: {directory}");
            var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"
                        or ".orchestrator-worktrees" or "TestResults" or "playwright-report"))
                .ToArray();
            Assert.NotEmpty(files);
            offenders.AddRange(files
                .Where(file =>
                {
                    var source = File.ReadAllText(file);
                    return source.Contains("DotnetBuildEnvironmentManager.", StringComparison.Ordinal) ||
                        source.Contains("GoalAcceptanceVerifier.", StringComparison.Ordinal);
                })
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/')));
        }

        Assert.True(offenders.Count == 0,
            $"Processes and Workers must not reference DotnetBuildEnvironmentManager or GoalAcceptanceVerifier. Offending files: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ProcessesAndWorkersSources_DoNotContainWorkspaceConsumerTokens()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var directories = new[] { "Processes", "Workers" }
            .Select(area => Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", area))
            .ToArray();
        var tokens = new[] { "DotnetBuildEnvironment", "GoalAcceptanceVerifier.", "LocalProcessVerifier" };
        var offenders = new List<string>();
        foreach (var directory in directories)
        {
            Assert.True(Directory.Exists(directory), $"Missing source directory: {directory}");
            var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"
                        or ".orchestrator-worktrees" or "TestResults" or "playwright-report"))
                .ToArray();
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                var source = File.ReadAllText(file);
                foreach (var token in tokens)
                {
                    if (source.Contains(token, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: {token}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Processes and Workers source directories ({string.Join(", ", directories)}) must not contain DotnetBuildEnvironment, GoalAcceptanceVerifier. or LocalProcessVerifier. Offending files and tokens: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void ProcessesAndWorkersSources_DoNotReferenceGoalWorktrees()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var area in new[] { "Processes", "Workers" })
        {
            var directory = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", area);
            Assert.True(Directory.Exists(directory), $"Missing source directory: {directory}");
            var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype"
                        or ".orchestrator-worktrees" or "TestResults" or "playwright-report"))
                .ToArray();
            Assert.NotEmpty(files);
            offenders.AddRange(files
                .Where(file => File.ReadAllText(file).Contains("GoalWorktrees.", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, file)));
        }

        Assert.True(offenders.Count == 0,
            $"Processes and Workers must not reference GoalWorktrees. Offending files: {string.Join(", ", offenders)}");
    }
}
