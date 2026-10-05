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
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyInfo.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyTempRedirectTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalBoard.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.GoalLifecycleCommands.cs",
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
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.HumanInputSupersede.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.PortfolioCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.SubscriptionDispatchCommands.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.TerminalSweepCommands.cs",
        });

    internal static IReadOnlyList<string> SeededAmbientRepositoryRootFiles { get; } = Array.AsReadOnly(
        new[]
        {
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DotnetBuildEnvironmentManagerTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PostLandingCanaryTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessStartInfoSourceGuardTests.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessTreeGuiSuppressionTests.cs",
            "tests/Mcg.AgentOrchestrator.TestSupport/SharedTestSupport.cs",
        });

    internal static IReadOnlyList<RepositoryLayoutViolation> Evaluate(
        string repositoryRoot,
        IEnumerable<string> multiPublicTypeFiles,
        IEnumerable<string> dottedFileNames,
        IEnumerable<string> ambientRepositoryRootFiles)
    {
        var multiTypeInventory = multiPublicTypeFiles.ToHashSet(StringComparer.Ordinal);
        var dottedNameInventory = dottedFileNames.ToHashSet(StringComparer.Ordinal);
        var ambientRootInventory = ambientRepositoryRootFiles.ToHashSet(StringComparer.Ordinal);
        var activeMultiTypeViolations = new HashSet<string>(StringComparer.Ordinal);
        var activeDottedNameViolations = new HashSet<string>(StringComparer.Ordinal);
        var activeAmbientRootViolations = new HashSet<string>(StringComparer.Ordinal);
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

            if (!TryReadTestSource(sourcePath, relativePath, violations, out var syntaxRoot, out var typeNames))
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

            var ambientRootFindings = FindAmbientRepositoryRootResolution(syntaxRoot);
            if (ambientRootFindings.Count > 0)
            {
                activeAmbientRootViolations.Add(relativePath);
                if (!ambientRootInventory.Contains(relativePath))
                {
                    violations.Add(new RepositoryLayoutViolation(
                        relativePath,
                        "ambient-repository-root-resolution",
                        $"Test source '{relativePath}' derives a repository root from ambient process state in " +
                        $"{string.Join(", ", ambientRootFindings)}. Seed repository walks from [CallerFilePath] instead; " +
                        "read tests/Mcg.AgentOrchestrator.Core.Tests/AgentHarnessDocsDriftTests.cs for the sanctioned " +
                        $"source-path pattern. See {DocumentationPath}."));
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
        AddStaleInventoryViolations(
            ambientRootInventory,
            activeAmbientRootViolations,
            unreadableFiles,
            "ambient-repository-root-resolution",
            nameof(SeededAmbientRepositoryRootFiles),
            violations);

        return violations;
    }

    private static bool TryReadTestSource(
        string sourcePath,
        string relativePath,
        ICollection<RepositoryLayoutViolation> violations,
        out CompilationUnitSyntax syntaxRoot,
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
            syntaxRoot = SyntaxFactory.CompilationUnit();
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
            syntaxRoot = SyntaxFactory.CompilationUnit();
            return false;
        }

        syntaxRoot = syntaxTree.GetCompilationUnitRoot();
        typeNames = syntaxRoot.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(IsTopLevelPublicType)
            .Select(GetTypeName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static IReadOnlyList<string> FindAmbientRepositoryRootResolution(CompilationUnitSyntax syntaxRoot)
    {
        var findings = new List<string>();
        foreach (var member in syntaxRoot.DescendantNodes().Where(IsMemberScope))
        {
            var nodes = member.DescendantNodes(node => node == member || !IsMemberScope(node)).ToArray();
            var ambientReads = nodes
                .OfType<ExpressionSyntax>()
                .Where(IsAmbientRead)
                .ToArray();
            if (ambientReads.Length == 0)
            {
                continue;
            }

            var hasAncestorWalk = nodes.Any(IsAncestorWalk);
            var hasRepositoryReach = HasRepositoryReach(nodes, GetMemberName(member));
            var hasRootNamedSink = ambientReads.Any(read => IsPassedToRootNamedSink(read, member));
            if (!hasRootNamedSink && (!hasAncestorWalk || !hasRepositoryReach))
            {
                continue;
            }

            findings.AddRange(ambientReads.Select(read => $"'{GetMemberName(member)}' ({read})"));
        }

        return findings.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static bool IsMemberScope(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax;

    private static string GetMemberName(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        DestructorDeclarationSyntax destructor => $"~{destructor.Identifier.ValueText}",
        LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
        AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
        OperatorDeclarationSyntax @operator => $"operator {@operator.OperatorToken.ValueText}",
        ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
        _ => member.Kind().ToString()
    };

    private static bool IsAmbientRead(ExpressionSyntax expression)
    {
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Parent is AssignmentExpressionSyntax assignment && assignment.Left == memberAccess)
            {
                return false;
            }

            var qualifier = RightmostIdentifier(memberAccess.Expression);
            return (qualifier == "Environment" && memberAccess.Name.Identifier.ValueText == "CurrentDirectory") ||
                (qualifier == "AppContext" && memberAccess.Name.Identifier.ValueText == "BaseDirectory");
        }

        return expression is InvocationExpressionSyntax invocation &&
            IsInvocationOnType(invocation, "Directory", "GetCurrentDirectory");
    }

    private static bool IsAncestorWalk(SyntaxNode node)
    {
        if (node is MemberAccessExpressionSyntax memberAccess && memberAccess.Name.Identifier.ValueText == "Parent")
        {
            return true;
        }

        if (node is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        return IsInvocationOnType(invocation, "Directory", "GetParent") ||
            (IsInvocationOnType(invocation, "Path", "Combine") &&
             invocation.ArgumentList.Arguments.Any(argument => IsStringLiteral(argument.Expression, "..")));
    }

    private static bool HasRepositoryReach(IReadOnlyCollection<SyntaxNode> nodes, string memberName)
    {
        if (ContainsRootName(memberName))
        {
            return true;
        }

        if (nodes.OfType<LiteralExpressionSyntax>().Any(IsRepositoryMarkerLiteral))
        {
            return true;
        }

        return nodes.OfType<InvocationExpressionSyntax>().Any(invocation =>
            IsInvocationOnType(invocation, "Path", "Combine") &&
            invocation.ArgumentList.Arguments.Count > 1 &&
            invocation.ArgumentList.Arguments[0].Expression is MemberAccessExpressionSyntax firstArgument &&
            firstArgument.Name.Identifier.ValueText == "FullName" &&
            invocation.ArgumentList.Arguments.Skip(1).Any(argument => IsRepositoryDirectoryLiteral(argument.Expression)));
    }

    private static bool IsPassedToRootNamedSink(ExpressionSyntax ambientRead, SyntaxNode member)
    {
        var argument = ambientRead.Ancestors()
            .TakeWhile(ancestor => ancestor != member)
            .OfType<ArgumentSyntax>()
            .FirstOrDefault();
        return argument?.Parent?.Parent is InvocationExpressionSyntax invocation &&
            ContainsRootName(GetInvocationName(invocation));
    }

    private static bool IsInvocationOnType(InvocationExpressionSyntax invocation, string typeName, string methodName) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
        memberAccess.Name.Identifier.ValueText == methodName &&
        RightmostIdentifier(memberAccess.Expression) == typeName;

    private static string GetInvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => string.Empty
    };

    private static string RightmostIdentifier(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
        _ => string.Empty
    };

    private static bool ContainsRootName(string value) =>
        value.Contains("RepositoryRoot", StringComparison.Ordinal) ||
        value.Contains("RepoRoot", StringComparison.Ordinal) ||
        value.Contains("SourceRoot", StringComparison.Ordinal);

    private static bool IsRepositoryMarkerLiteral(LiteralExpressionSyntax literal)
    {
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return false;
        }

        var value = literal.Token.ValueText;
        return value is ".git" or "AGENTS.md" or "CLAUDE.md" or "global.json" or "Directory.Build.props" ||
            value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRepositoryDirectoryLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.StringLiteralExpression) &&
        literal.Token.ValueText is "tests" or "src" or "docs" or "config" or "scripts";

    private static bool IsStringLiteral(ExpressionSyntax expression, string value) =>
        expression is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.StringLiteralExpression) &&
        literal.Token.ValueText == value;

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
