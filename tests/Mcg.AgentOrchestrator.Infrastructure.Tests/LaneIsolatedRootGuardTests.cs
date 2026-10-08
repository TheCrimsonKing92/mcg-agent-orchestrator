using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class LaneIsolatedRootGuardTests
{
    private const string RootVariable = "MCG_DOTNET_ISOLATED_ROOT";
    private const string ExplicitStorage =
        "Owns a GUID DotnetBuildStorageRoot via DotnetBuildEnvironmentManagerRootedTestBase; its separate collection protects process-local hooks.";

    // Waivers are named, reviewable, and rejected when no longer needed. They do not waive the clear scan.
    private static readonly IReadOnlyDictionary<Type, string> CollectionExceptions = new Dictionary<Type, string>
    {
        [typeof(DotnetBuildEnvironmentManagerTests)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsLockAttributionLandingFixtures)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsOwnedRunRootRegistration)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsSlotsBusyLiveness)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsStableSlotArtifacts)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsWriteFailureScope)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsStableSlotHolderLabel)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsLeasePermitsStaleRecovery)] = ExplicitStorage,
        [typeof(DotnetBuildEnvironmentManagerTestsFocusedRunner)] =
            "Owns explicit storage through the rooted base; child runners pin their own override or GUID LOCALAPPDATA fallback, not the host grid.",
        [typeof(DotnetBuildEnvironmentManagerTestsProcessSpawnGuard)] = ExplicitStorage,
        [typeof(LocalProcessVerifierStaticHookTests)] = ExplicitStorage,
        [typeof(StructuralCoveragePermitWaitTests)] = ExplicitStorage,
        [typeof(ConductorBatchLoopTestsDecisionProgressLine)] = "Formats decisions; no build or slot acquisition.",
        [typeof(ConductorBatchLoopTestsAuthorAnswer)] = "Exercises author policy with injected conductor actions; no real build.",
        [typeof(ConductorBatchLoopTestsActivationHeartbeat)] = "Uses fake conductor/heartbeat callbacks; no real build.",
        [typeof(ConductorBatchLoopTestsActivationHeartbeatBeforeSleep)] = "Uses fake sleep and heartbeat callbacks; no real build.",
        [typeof(ConductorBatchLoopTestsActivationHeartbeatBeforeSleepOffTickCadence)] = "Uses fake sleep and heartbeat callbacks; no real build.",
        [typeof(ConductorBatchLoopTestsIdleActivationHeartbeat)] = "Uses injected idle-loop callbacks; no real build.",
        [typeof(ConductorBatchLoopTestsConsoleCodePageChange)] = "Uses a GUID log root and injected conductor actions; no real build.",
        [typeof(ConductorBatchLoopTestsPromptRolloutWatchLanding)] = "Uses fake conductor/landing callbacks; no real build.",
        [typeof(ConductorBatchLoopTestsParallelAcceptanceHoldOwner)] = "Checks hold policy with fake acceptance actions; no real build.",
        [typeof(ConductorBatchLoopTestsStewardCaseEClose)] = "Checks steward policy with injected actions; no real build.",
        [typeof(ConductorBatchLoopTestsStewardCaseDClose)] = "Uses StewardCaseDHarness with GUID stores and injected actions; no real build.",
        [typeof(ConductorBatchLoopTestsStewardCaseFRoute)] = "Uses StewardCaseFHarness with GUID stores, a fake model, and injected driver actions; no real build or slot acquisition.",
        [typeof(ConductorBatchLoopTestsStaleProcessReconcile)] = "Uses fake process reconciliation and conductor actions; no real build.",
        [typeof(ConductorBatchLoopTestsSpawnLaunchName)] = "Checks dispatch metadata with injected launch actions; no real build.",
        [typeof(ConductorBatchLoopTestsRetryReservationReadmit)] = "Uses fake retry/dispatch actions; no real build.",
        [typeof(ConductorBatchLoopTestsUnappliedExitStream)] = "Uses fake exit-stream/conductor actions; no real build.",
        [typeof(ConductorBatchLoopTestsMainSuspectRelease)] = "Checks release policy with injected actions; no real build.",
        [typeof(OperatorIntentAdjudicationTestsStewardCaseD)] = "Uses StewardCaseDHarness with GUID stores and injected actions; no real build."
    };

    private static readonly IReadOnlyDictionary<(Type Class, string Method), string> ChildClearExceptions =
        new Dictionary<(Type, string), string>
        {
            [(typeof(DotnetBuildEnvironmentManagerTestsFocusedRunner),
                nameof(DotnetBuildEnvironmentManagerTestsFocusedRunner.FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt))] =
                "The child removes its override to test fallback, then redirects LOCALAPPDATA to a GUID profile (FocusedRunner.cs:275-286,402-403). The parent fixture is unchanged."
        };

    [Xunit.Fact]
    public void Effective_lane_classes_keep_isolated_roots_or_named_exceptions()
    {
        var repository = VerifiedRepositoryRoot.Find();
        Assert.True(File.Exists(Path.Combine(repository, "config", "acceptance-manifest.json")),
            $"Acceptance manifest missing under '{repository}'.");
        var lane = Assert.Single(AcceptanceGateEngineSettings.Load(repository).InfrastructureTestLanes,
            item => item.Name == "Dotnet build slots");
        var testTypes = typeof(LaneIsolatedRootGuardTests).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && (type.IsPublic || type.IsNestedPublic))
            .Where(type => type.GetMethods().Any(method => method.GetCustomAttributes(inherit: true)
                .Any(attribute => attribute is Xunit.FactAttribute or Xunit.TheoryAttribute)))
            .ToArray();
        ValidateFilter(lane.Filter, testTypes);
        var resolvedLanes = AcceptanceLaneMembership.ResolveOwnedCollections([lane],
            AcceptanceTestClassSourceScanner.Scan(repository));
        var classes = testTypes.Where(type => AcceptanceLaneMembership.LanesIncluding(resolvedLanes, type.FullName!).Count > 0)
            .ToArray();
        Assert.NotEmpty(classes);
        foreach (var (type, reason) in CollectionExceptions)
        {
            Assert.True(classes.Contains(type), $"Stale collection exception: {type.FullName} is outside the lane.");
            Assert.False(IsIsolatedCollection(type), $"Stale collection exception: {type.FullName} now has the isolated collection.");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Missing exception reason: {type.FullName}.");
        }

        var sources = AcceptanceTestClassSourceScanner.ScanSources(repository).ToDictionary(item => item.FullName);
        var usedChildExceptions = new HashSet<(Type, string)>();
        foreach (var type in classes)
        {
            Assert.True(IsIsolatedCollection(type) || CollectionExceptions.ContainsKey(type),
                $"{type.FullName} is not in {TestCollections.DotnetBuildSlots} and has no named isolation exception.");
            Assert.True(sources.TryGetValue(type.FullName!, out var source), $"Missing source for {type.FullName}.");
            Assert.NotEmpty(source!.SourcePaths);
            var hierarchy = new HashSet<string>(StringComparer.Ordinal);
            for (var parent = type; parent is not null; parent = parent.BaseType)
                hierarchy.Add(parent.FullName!);
            foreach (var path in source.SourcePaths)
            {
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(repository, path))).GetRoot();
                Assert.False(syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error),
                    $"Cannot parse source for {type.FullName}: {path}.");
                foreach (var declaration in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
                             .Where(node => hierarchy.Contains(ClassName(node))))
                foreach (var clear in FindUnsafeClears(declaration))
                {
                    var method = clear.Node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                    var key = (type, method ?? "");
                    if (clear.Child && IsFocusedChildFallback(clear.Node) && ChildClearExceptions.TryGetValue(key, out var reason))
                    {
                        Assert.False(string.IsNullOrWhiteSpace(reason));
                        usedChildExceptions.Add(key);
                        continue;
                    }
                    var line = clear.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    Assert.Fail($"{type.FullName} clears {RootVariable} outside a restoring scope at {path}:{line}.");
                }
            }
        }
        foreach (var key in ChildClearExceptions.Keys)
            Assert.True(usedChildExceptions.Contains(key), $"Stale child-clear exception: {key.Class.FullName}.{key.Method}.");
    }

    private static bool IsIsolatedCollection(Type type) =>
        type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name == TestCollections.DotnetBuildSlots;

    private static void ValidateFilter(string filter, Type[] testTypes)
    {
        var arguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter).ToArray();
        Assert.True(arguments.Length > 0 && arguments.Length % 2 == 0, $"Empty or incomplete lane filter: '{filter}'.");
        for (var index = 0; index < arguments.Length; index += 2)
        {
            Assert.True(arguments[index] == "--filter-class", $"Unsupported lane token '{arguments[index]}'.");
            var pattern = arguments[index + 1];
            Assert.True(testTypes.Any(type => pattern.StartsWith('*') && pattern.EndsWith('*')
                    ? type.FullName!.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase)
                    : type.FullName == pattern),
                $"Lane token '{pattern}' resolves to no test class.");
        }
    }

    private static string ClassName(ClassDeclarationSyntax node) => string.Join(".",
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString())
            .Append(string.Join("+", node.Ancestors().OfType<ClassDeclarationSyntax>().Reverse()
                .Select(item => item.Identifier.ValueText).Append(node.Identifier.ValueText))));

    private static bool IsRootVariable(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.Token.ValueText == RootVariable,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == nameof(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable),
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == nameof(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable),
        _ => false
    };

    private static bool IsEmpty(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NullLiteralExpression) || literal.IsKind(SyntaxKind.DefaultLiteralExpression) ||
            literal.IsKind(SyntaxKind.StringLiteralExpression) && string.IsNullOrWhiteSpace(literal.Token.ValueText),
        DefaultExpressionSyntax value => value.Type.ToString() is "string" or "String" or "System.String",
        MemberAccessExpressionSyntax member => member.ToString() is "string.Empty" or "String.Empty" or "System.String.Empty",
        ParenthesizedExpressionSyntax parenthesized => IsEmpty(parenthesized.Expression),
        _ => false
    };

    private static bool IsChildEnvironment(ExpressionSyntax expression) => expression is MemberAccessExpressionSyntax member &&
        member.Name.Identifier.ValueText is "Environment" or "EnvironmentVariables";

    private static bool IsSetVariable(InvocationExpressionSyntax call) => call.Expression.ToString() is
        "Environment.SetEnvironmentVariable" or "System.Environment.SetEnvironmentVariable";

    private static bool IsFocusedChildFallback(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax call || call.Expression.ToString() != "startInfo.Environment.Remove" ||
            call.Parent is not ExpressionStatementSyntax statement || statement.Parent is not BlockSyntax block)
            return false;
        var index = block.Statements.IndexOf(statement);
        if (index + 1 >= block.Statements.Count || block.Statements[index + 1] is not ExpressionStatementSyntax next ||
            next.Expression is not AssignmentExpressionSyntax redirect ||
            redirect.Left.ToString() != "startInfo.Environment[\"LOCALAPPDATA\"]" || redirect.Right.ToString() != "localAppData")
            return false;
        var declarations = block.AncestorsAndSelf().OfType<BlockSyntax>()
            .SelectMany(ancestor => ancestor.Statements.OfType<LocalDeclarationStatementSyntax>())
            .Where(item => item.Span.End < call.SpanStart).SelectMany(item => item.Declaration.Variables).ToArray();
        return declarations.Any(variable => variable.Identifier.ValueText == "localAppData" &&
                   variable.Initializer?.Value.ToString() == "Path.Combine(sharedProfileRoot, \"Local\")") &&
               declarations.Any(variable => variable.Identifier.ValueText == "sharedProfileRoot" &&
                   variable.Initializer?.Value.DescendantNodes().OfType<InvocationExpressionSyntax>()
                       .Any(invocation => invocation.Expression.ToString() == "Guid.NewGuid") == true);
    }

    private static IEnumerable<(SyntaxNode Node, bool Child)> FindUnsafeClears(SyntaxNode source)
    {
        foreach (var call in source.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var args = call.ArgumentList.Arguments;
            if (args.Count == 0 || !IsRootVariable(args[0].Expression)) continue;
            if (IsSetVariable(call) && args.Count >= 2 && IsEmpty(args[1].Expression) && !RestoredInFinally(call))
                yield return (call, false);
            else if (call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Remove" &&
                     IsChildEnvironment(member.Expression))
                yield return (call, true);
            else if (call.Expression is MemberAccessExpressionSyntax scope && scope.Expression.ToString().EndsWith("EnvVarScope", StringComparison.Ordinal) &&
                     scope.Name.Identifier.ValueText == "ForVariable" && args.Count >= 2 && IsEmpty(args[1].Expression) && !IsUsingResource(call))
                yield return (call, false);
        }
        foreach (var creation in source.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var args = creation.ArgumentList?.Arguments;
            if (creation.Type.ToString().Split('.').Last() == "EnvVarScope" && args is { Count: >= 2 } &&
                IsRootVariable(args.Value[0].Expression) && IsEmpty(args.Value[1].Expression) && !IsUsingResource(creation))
                yield return (creation, false);
        }
        foreach (var assignment in source.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            if (assignment.Left is ElementAccessExpressionSyntax element && IsChildEnvironment(element.Expression) &&
                element.ArgumentList.Arguments.Count == 1 && IsRootVariable(element.ArgumentList.Arguments[0].Expression) && IsEmpty(assignment.Right))
                yield return (assignment, true);
    }

    private static bool IsUsingResource(SyntaxNode node) =>
        node.Ancestors().OfType<UsingStatementSyntax>().Any(statement =>
            statement.Expression == node || statement.Declaration?.Variables.Any(variable => variable.Initializer?.Value == node) == true) ||
        node.Ancestors().OfType<LocalDeclarationStatementSyntax>().Any(statement =>
            statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) &&
            statement.Declaration.Variables.Any(variable => variable.Initializer?.Value == node));

    private static bool RestoredInFinally(InvocationExpressionSyntax clear)
    {
        foreach (var attempt in clear.Ancestors().OfType<TryStatementSyntax>())
        {
            if (attempt.Finally is null || !attempt.Block.Span.Contains(clear.Span)) continue;
            if (attempt.Finally.Block.Statements.FirstOrDefault() is ExpressionStatementSyntax finalStatement &&
                finalStatement.Expression is InvocationExpressionSyntax restore)
            {
                var args = restore.ArgumentList.Arguments;
                if (!IsSetVariable(restore) || args.Count < 2 || !IsRootVariable(args[0].Expression) ||
                    args[1].Expression is not IdentifierNameSyntax captured) continue;
                // Require a capture before the try, in the same lexical block, not an arbitrary "original" variable.
                if (attempt.Parent is BlockSyntax block && block.Statements.OfType<LocalDeclarationStatementSyntax>()
                    .Where(statement => statement.Span.End < attempt.SpanStart)
                    .SelectMany(statement => statement.Declaration.Variables).Any(variable =>
                        variable.Identifier.ValueText == captured.Identifier.ValueText &&
                        variable.Initializer?.Value is InvocationExpressionSyntax read &&
                        read.Expression.ToString() is "Environment.GetEnvironmentVariable" or "System.Environment.GetEnvironmentVariable" &&
                        read.ArgumentList.Arguments.Count == 1 && IsRootVariable(read.ArgumentList.Arguments[0].Expression)) &&
                    !block.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                        assignment.SpanStart < restore.SpanStart && assignment.Left.ToString() == captured.Identifier.ValueText) &&
                    !block.DescendantNodes().OfType<ArgumentSyntax>().Any(argument =>
                        argument.SpanStart < restore.SpanStart && argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword &&
                        argument.Expression.ToString() == captured.Identifier.ValueText))
                    return true;
            }
        }
        return false;
    }

    [Xunit.Theory]
    [Xunit.InlineData("Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null);", 1)]
    [Xunit.InlineData("Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", default(string));", 1)]
    [Xunit.InlineData("Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, string.Empty);", 1)]
    [Xunit.InlineData("Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", \" \" );", 1)]
    [Xunit.InlineData("startInfo.Environment.Remove(\"MCG_DOTNET_ISOLATED_ROOT\");", 1)]
    [Xunit.InlineData("startInfo.Environment[\"MCG_DOTNET_ISOLATED_ROOT\"] = null;", 1)]
    [Xunit.InlineData("new EnvVarScope(\"MCG_DOTNET_ISOLATED_ROOT\", null);", 1)]
    [Xunit.InlineData("using var scope = new EnvVarScope(\"MCG_DOTNET_ISOLATED_ROOT\", null);", 0)]
    [Xunit.InlineData("using var scope = new OtherScope(new EnvVarScope(\"MCG_DOTNET_ISOLATED_ROOT\", null));", 1)]
    [Xunit.InlineData("using (new EnvVarScope(\"MCG_DOTNET_ISOLATED_ROOT\", \"\")) { }", 0)]
    [Xunit.InlineData("using var scope = EnvVarScope.ForVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null);", 0)]
    [Xunit.InlineData("using (var unrelated = new OtherScope()) { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null); }", 1)]
    [Xunit.InlineData("var prior = Environment.GetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\"); try { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null); } finally { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", prior); }", 0)]
    [Xunit.InlineData("var prior = \"wrong\"; try { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null); } finally { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", prior); }", 1)]
    [Xunit.InlineData("var prior = Environment.GetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\"); try { prior = null; Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null); } finally { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", prior); }", 1)]
    [Xunit.InlineData("// Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null);\nvar text = \"null\";", 0)]
    public void Clear_scan_distinguishes_restoration_from_unscoped_clears(string body, int violations)
    {
        var source = CSharpSyntaxTree.ParseText("class Probe { void Test() { " + body + " } }").GetRoot();
        Assert.Equal(violations, FindUnsafeClears(source).Count());
    }

    [Xunit.Theory]
    [Xunit.InlineData("FullyQualifiedName~NoSuchLaneGuardClass")]
    [Xunit.InlineData("Category!=Other")]
    [Xunit.InlineData("Name~SomeMethod")]
    [Xunit.InlineData("")]
    public void Filter_guard_rejects_missing_classes_and_unknown_syntax(string filter)
    {
        Assert.NotNull(Record.Exception(() => ValidateFilter(filter, [typeof(LaneIsolatedRootGuardTests)])));
    }
}
