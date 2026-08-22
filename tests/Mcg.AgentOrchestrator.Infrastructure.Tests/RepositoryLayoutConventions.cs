using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class RepositoryLayoutConventions
{
    internal const string DocumentationPath = "docs/repository-conventions.md";

    // Seeded from the test tree on 2026-08-22. New entries are forbidden. Delete an entry as soon as
    // its file becomes compliant; stale entries fail evaluation so these inventories can only shrink.
    internal static IReadOnlyList<string> SeededMultiPublicTypeFiles { get; } = Array.AsReadOnly(
        new[]
        {
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/AssemblyInfo.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/DashboardRenderingTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyInfo.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyTempRedirectTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalBoard.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalLifecycleCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/RealProcessShardProbe/ShardProbeTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalRefinementTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTestsSqliteTooling.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LocalProcessVerifierTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs",
            "tests/Mcg.AgentOrchestrator.TestSupport/SharedTestSupport.cs",
        });

    internal static IReadOnlyList<string> SeededDottedFileNames { get; } = Array.AsReadOnly(
        new[]
        {
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/CliCommandTests.AddTaskCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.AttentionCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.BacklogIntakeCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalBoard.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalLifecycleCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalRevision.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.HumanInputSupersede.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PersistentRunnerCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PortfolioCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.RefreshDispatchOutput.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.SubscriptionDispatchCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.TerminalSweepCommands.cs",
        });

    internal static IReadOnlyList<RepositoryLayoutViolation> Evaluate(
        string repositoryRoot,
        IEnumerable<string> multiPublicTypeFiles,
        IEnumerable<string> dottedFileNames)
    {
        var multiTypeInventory = multiPublicTypeFiles.ToHashSet(StringComparer.Ordinal);
        var dottedNameInventory = dottedFileNames.ToHashSet(StringComparer.Ordinal);
        var activeMultiTypeViolations = new HashSet<string>(StringComparer.Ordinal);
        var activeDottedNameViolations = new HashSet<string>(StringComparer.Ordinal);
        var unreadableFiles = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<RepositoryLayoutViolation>();
        var testsRoot = Path.Combine(repositoryRoot, "tests");

        foreach (var sourcePath in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relativePath = NormalizePath(Path.GetRelativePath(repositoryRoot, sourcePath));
            if (IsGeneratedOrArtifactPath(relativePath))
            {
                continue;
            }

            if (!TryReadTopLevelPublicTypeNames(sourcePath, relativePath, violations, out var typeNames))
            {
                unreadableFiles.Add(relativePath);
                continue;
            }

            if (typeNames.Count > 1)
            {
                activeMultiTypeViolations.Add(relativePath);
                if (!multiTypeInventory.Contains(relativePath))
                {
                    violations.Add(new RepositoryLayoutViolation(
                        relativePath,
                        "multiple-top-level-public-types",
                        $"Test source '{relativePath}' declares multiple top-level public types: " +
                        $"{string.Join(", ", typeNames)}. Move each public type to its own dot-free file. " +
                        $"See {DocumentationPath}."));
                }
            }

            var fileStem = Path.GetFileNameWithoutExtension(relativePath);
            if (typeNames.Count > 0 && fileStem.Contains('.', StringComparison.Ordinal))
            {
                activeDottedNameViolations.Add(relativePath);
                if (!dottedNameInventory.Contains(relativePath))
                {
                    violations.Add(new RepositoryLayoutViolation(
                        relativePath,
                        "dotted-test-file-name",
                        $"Test source '{relativePath}' produces the filename-derived filter " +
                        $"'FullyQualifiedName~{fileStem}', but actually declares: {string.Join(", ", typeNames)}. " +
                        "Rename or split the file so its dot-free stem is the public test type name. " +
                        $"See {DocumentationPath}."));
                }
            }
        }

        AddStaleInventoryViolations(
            multiTypeInventory,
            activeMultiTypeViolations,
            unreadableFiles,
            "multiple-top-level-public-types",
            nameof(SeededMultiPublicTypeFiles),
            violations);
        AddStaleInventoryViolations(
            dottedNameInventory,
            activeDottedNameViolations,
            unreadableFiles,
            "dotted-test-file-name",
            nameof(SeededDottedFileNames),
            violations);

        return violations;
    }

    private static bool TryReadTopLevelPublicTypeNames(
        string sourcePath,
        string relativePath,
        ICollection<RepositoryLayoutViolation> violations,
        out IReadOnlyList<string> typeNames)
    {
        string source;
        try
        {
            source = File.ReadAllText(sourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            violations.Add(new RepositoryLayoutViolation(
                relativePath,
                "unreadable-test-source",
                $"Test source '{relativePath}' could not be read ({exception.GetType().Name}: {exception.Message}). " +
                $"The layout guard cannot pass without checking it. See {DocumentationPath}."));
            typeNames = [];
            return false;
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: sourcePath);
        var errors = syntaxTree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();
        if (errors.Length > 0)
        {
            violations.Add(new RepositoryLayoutViolation(
                relativePath,
                "unparseable-test-source",
                $"Test source '{relativePath}' could not be parsed: {string.Join(" | ", errors)}. " +
                $"The layout guard cannot pass without checking it. See {DocumentationPath}."));
            typeNames = [];
            return false;
        }

        var root = syntaxTree.GetCompilationUnitRoot();
        typeNames = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(IsTopLevelPublicType)
            .Select(GetTypeName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool IsTopLevelPublicType(MemberDeclarationSyntax declaration)
    {
        if (declaration is not BaseTypeDeclarationSyntax and not DelegateDeclarationSyntax)
        {
            return false;
        }

        return declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) &&
            !declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any();
    }

    private static string GetTypeName(MemberDeclarationSyntax declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
        _ => throw new InvalidOperationException($"Unsupported public type declaration {declaration.Kind()}.")
    };

    private static void AddStaleInventoryViolations(
        IEnumerable<string> inventory,
        IReadOnlySet<string> activeViolations,
        IReadOnlySet<string> unreadableFiles,
        string rule,
        string inventoryName,
        ICollection<RepositoryLayoutViolation> violations)
    {
        foreach (var relativePath in inventory.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (activeViolations.Contains(relativePath) || unreadableFiles.Contains(relativePath))
            {
                continue;
            }

            violations.Add(new RepositoryLayoutViolation(
                relativePath,
                $"stale-{rule}-inventory",
                $"Grandfathered entry '{relativePath}' no longer violates '{rule}' or no longer exists. " +
                $"Delete it from {nameof(RepositoryLayoutConventions)}.{inventoryName} in the same change; " +
                $"the inventory may only shrink. See {DocumentationPath}."));
        }
    }

    private static bool IsGeneratedOrArtifactPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".scratch", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".orchestrator-prototype", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("playwright-report", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fileName = Path.GetFileName(relativePath);
        return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}

internal sealed record RepositoryLayoutViolation(string RelativePath, string Rule, string Message);
