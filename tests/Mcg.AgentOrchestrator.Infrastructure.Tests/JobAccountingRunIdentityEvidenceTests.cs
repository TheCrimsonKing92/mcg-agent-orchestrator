using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class JobAccountingRunIdentityEvidenceTests
{
    private const string AnonymousJob =
        "OwnedProcessGroup creates an anonymous CreateJobObjectW handle (null name); its kernel handle is unique, not a GUID-derived name. This source audit does not prove child exit or handle cleanup.";
    private const string ScenarioRoot =
        "The private Scenario passes Root/HostRoot through its constructor and resolver; following those caller/constructor edges exceeds this audit's one-helper boundary. Overlap/cleanup evidence remains owed.";

    // Keyed by class AND site kind: an anonymous-job waiver never exempts that class's PID paths.
    private static readonly IReadOnlyDictionary<(Type Class, string Kind), string> Exceptions =
        new Dictionary<(Type, string), string>
        {
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsSlotGateJobResources), "job")] = AnonymousJob,
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsSharedApparatusInvalidation), "job")] = AnonymousJob,
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsVerdictAndBuildCache), "job")] = AnonymousJob,
            [(typeof(GoalAcceptanceVerifierTestsCaptureTempRoot), "job")] = AnonymousJob,
            [(typeof(GoalAcceptanceVerifierTestsFocusedCaptureLimit), "job")] = AnonymousJob,
            [(typeof(GoalAcceptanceVerifierTestsProcessInvokerEquivalence), "job")] = AnonymousJob,
            [(typeof(GoalAcceptanceVerifierTestsCmdCommandLineLimit), "job")] = AnonymousJob,
            [(typeof(HermeticVerificationEnvironmentTestsSystemLocations), "job")] = AnonymousJob,
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorClaim), "occupancy")] = ScenarioRoot,
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorSlots), "occupancy")] = ScenarioRoot,
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLanePolicy), "occupancy")] = ScenarioRoot,
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsShardReceipts), "path")] =
                "ShardReceiptsUseAttemptArtifactsInsteadOfReleasableSlot supplies a fixed temp/slot-1/lease.lock as a path-only DotnetBuildEnvironment input; it does not acquire that lock. Other helper root parameters require caller tracing."
        };

    [Xunit.Fact]
    public void Lane_job_pid_and_slot_sites_have_run_identity_or_live_named_exception()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var classes = new[] { "Goal acceptance verifier", "Goal acceptance build slots" }
            .SelectMany(lane => LaneIsolatedRootScanner.ResolveLaneClasses(repository, lane, GetType().Assembly))
            .Distinct().ToArray();
        var sources = AcceptanceTestClassSourceScanner.ScanSources(repository).ToDictionary(item => item.FullName);
        VerifyIdentityHelpers(repository);
        var used = new HashSet<(Type, string)>();
        var count = 0;
        var registrySiteProved = false;
        var proved = new HashSet<string>();
        foreach (var type in classes)
        {
            Assert.True(sources.TryGetValue(type.FullName!, out var source), $"Missing source for {type.FullName}.");
            Assert.NotEmpty(source!.SourcePaths);
            var hierarchy = new HashSet<string>();
            for (var parent = type; parent is not null; parent = parent.BaseType)
                hierarchy.Add(parent.FullName!);
            foreach (var path in source.SourcePaths)
            {
                var syntax = Parse(repository, path);
                foreach (var declaration in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
                             .Where(node => hierarchy.Contains(LaneIsolatedRootScanner.ClassName(node))))
                foreach (var site in CreationSites(declaration))
                {
                    count++;
                    var proof = site.Kind switch
                    {
                        "slot" => UsesFixtureStorage(site.Node, type),
                        "registry" or "path" => site.Identity is not null &&
                            IsRunPath(site.Identity, site.Node, declaration),
                        "dispatch" or "job" => IsRegistryScoped(site, declaration),
                        _ => false
                    };
                    if (proof)
                    {
                        proved.Add(site.Kind);
                        registrySiteProved |= type == typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsSlotGateJobResources) &&
                            site.Kind == "registry";
                        continue;
                    }
                    var key = (type, site.Kind);
                    Assert.True(Exceptions.TryGetValue(key, out var reason),
                        $"Unproven {site.Kind} identity: {type.FullName} at {path}:{Line(site.Node)}: {site.Node}.");
                    Assert.False(string.IsNullOrWhiteSpace(reason), $"Missing exception reason for {type.FullName}/{site.Kind}.");
                    if (site.Kind == "job")
                        Assert.True(site.Node is InvocationExpressionSyntax invocation &&
                            (invocation.Expression.ToString().StartsWith("GoalAcceptanceVerifier.RunProcess", StringComparison.Ordinal) ||
                             invocation.Expression.ToString() == "WorkerProcessJobs.TryRegister"),
                            $"Job exception does not cover a new job-creation API: {type.FullName} at {path}:{Line(site.Node)}.");
                    used.Add(key);
                }
            }
        }
        Assert.True(count > 0, "No job/PID/slot creation sites found in either lane.");
        Assert.Contains("registry", proved); // SlotGateJobResources' real PID registry, not just fake slot descriptors.
        Assert.True(registrySiteProved, "SlotGateJobResources has no proved per-run PID registry site.");
        Assert.Contains("slot", proved);
        Assert.Contains("path", proved);
        foreach (var key in Exceptions.Keys)
        {
            Assert.Contains(key.Class, classes);
            Assert.True(used.Contains(key), $"Stale job-accounting exception: {key.Class.FullName}/{key.Kind}.");
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Path.Combine(Path.GetTempPath(), \"shared.pid\")", false)]
    [Xunit.InlineData("Path.Combine(root, \"child.pid\")", true)]
    [Xunit.InlineData("Path.Combine(root, \"lease.lock\")", true)]
    [Xunit.InlineData("Path.Combine(root, \"../shared.pid\")", false)]
    [Xunit.InlineData("Path.Combine(root, \"C:/shared.pid\")", false)]
    public void Path_evidence_rejects_fixed_paths_and_accepts_guid_derived_paths(string expression, bool expected)
    {
        var syntax = CSharpSyntaxTree.ParseText(
            "class Probe { void Test() { var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(\"N\")); " +
            "var path = " + expression + "; } }").GetRoot();
        var declaration = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>());
        var variable = declaration.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(node => node.Identifier.ValueText == "path");
        Assert.Equal(expected, IsRunPath(variable.Initializer!.Value, variable, declaration));
    }

    [Xunit.Fact]
    public void Registry_scope_evidence_rejects_unscoped_dispatch_and_accepts_scoped_dispatch()
    {
        const string dispatch = "new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal, task, logs);";
        var unscopedSyntax = CSharpSyntaxTree.ParseText("class Probe { void Test() { " + dispatch + " } }").GetRoot();
        var unscopedClass = Assert.Single(unscopedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>());
        var unscopedSite = Assert.Single(CreationSites(unscopedClass));
        Assert.Equal("dispatch", unscopedSite.Kind);
        Assert.False(IsRegistryScoped(unscopedSite, unscopedClass));

        var scopedSyntax = CSharpSyntaxTree.ParseText(
            "class Probe { void Test() { using var registry = WorkerProcessJobs.UseRegistryScopeForTests(null); " +
            dispatch + " } }").GetRoot();
        var scopedClass = Assert.Single(scopedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>());
        var scopedSite = Assert.Single(CreationSites(scopedClass));
        Assert.Equal("dispatch", scopedSite.Kind);
        Assert.True(IsRegistryScoped(scopedSite, scopedClass));
    }

    private sealed record Site(string Kind, SyntaxNode Node, ExpressionSyntax? Identity = null);

    private static IEnumerable<Site> CreationSites(ClassDeclarationSyntax declaration)
    {
        foreach (var call in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = call.Expression.ToString();
            var shortName = name.Split('.').Last();
            var args = call.ArgumentList.Arguments;
            if (name == "WorkerProcessJobs.ConfigureRegistry")
                yield return new Site("registry", call, args.FirstOrDefault()?.Expression);
            else if (shortName is "AcquireFirstAvailableStableSlotExecutionLock" or "TryAcquireFirstAvailableStableSlotExecutionLock")
                yield return new Site("slot", call);
            else if (name is "RemoteExecutorOccupancy.TryClaimExclusive" or "RemoteExecutorOccupancy.Claim")
                yield return new Site("occupancy", call, args.FirstOrDefault()?.Expression);
            else if (shortName == "StartLatestDispatch")
                yield return new Site("dispatch", call);
            else if (name.StartsWith("WorkerProcessJobs.TryRegister", StringComparison.Ordinal) ||
                     shortName is "CreateJobObjectW" or "CreateKillOnCloseJob" ||
                     name.StartsWith("GoalAcceptanceVerifier.RunProcess", StringComparison.Ordinal))
                yield return new Site("job", call);
            else if (name == "Path.Combine" && args.Skip(1).Any(arg =>
                         arg.Expression is LiteralExpressionSyntax literal &&
                         (literal.Token.ValueText.EndsWith(".pid", StringComparison.OrdinalIgnoreCase) ||
                          literal.Token.ValueText.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) ||
                         arg.Expression is InterpolatedStringExpressionSyntax interpolated &&
                         interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Any(text =>
                             text.TextToken.ValueText.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))))
                yield return new Site("path", call, call);
        }
        foreach (var creation in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            if (creation.Type.ToString().Split('.').Last() is "OwnedProcessGroup" or "SpawnRegistry")
                yield return new Site(creation.Type.ToString().EndsWith("SpawnRegistry", StringComparison.Ordinal)
                    ? "registry" : "job", creation, creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
    }

    private static bool IsRegistryScoped(Site site, ClassDeclarationSyntax declaration) =>
        (site.Kind == "dispatch" || site.Kind == "job" &&
            site.Node is InvocationExpressionSyntax invocation &&
            invocation.Expression.ToString() == "WorkerProcessJobs.TryRegister") &&
        declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(call => call.Expression.ToString() == "WorkerProcessJobs.UseRegistryScopeForTests");

    private static bool UsesFixtureStorage(SyntaxNode node, Type type)
    {
        if (!LaneIsolatedRootScanner.IsIsolatedCollection(type) || node is not InvocationExpressionSyntax call)
            return false;
        // Explicit roots require their own evidence; positional storage roots are not silently waived.
        var storageIndex = call.Expression.ToString().EndsWith(".TryAcquireFirstAvailableStableSlotExecutionLock", StringComparison.Ordinal)
            ? 6 : 4;
        return !call.ArgumentList.Arguments.Any(arg => arg.NameColon?.Name.Identifier.ValueText == "storageRoot") &&
               call.ArgumentList.Arguments.Count(arg => arg.NameColon is null) <= storageIndex;
    }

    // Bounded expression tracing, not a call-graph claim: local initializers and one named root factory.
    // GUID components must stay below a temporary or already isolated parent.
    private static bool IsRunPath(ExpressionSyntax expression, SyntaxNode site, ClassDeclarationSyntax declaration,
        HashSet<string>? visited = null, int factoryHops = 0)
    {
        visited ??= [];
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            return IsRunPath(parenthesized.Expression, site, declaration, visited, factoryHops);
        if (expression is InvocationExpressionSyntax call)
        {
            var name = call.Expression.ToString();
            if (name == "Path.Combine")
            {
                var args = call.ArgumentList.Arguments;
                return args.Count > 0 &&
                    args.Skip(1).All(arg => IsRelativeComponent(arg.Expression)) &&
                    (IsRunPath(args[0].Expression, site, declaration, new HashSet<string>(visited), factoryHops) ||
                     args[0].Expression.ToString() == "Path.GetTempPath()" &&
                     args.Skip(1).Any(arg => HasGuid(arg.Expression)));
            }
            if (name.Split('.').Last() == "CreateTempDirectory") return true; // SharedTestSupport checked below.
            if (name.Split('.').Last() is "CreateManifestWorkspace" or "CreateTrackedManifestShapeWorkspace" or
                "CreateTwoLaneShardManifestWorkspace") return true; // Named scenario constructors checked below.
            var local = declaration.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == name).ToArray();
            // A same-class factory is one hop and must return the GUID-rooted local it created.
            if (factoryHops == 0 && local.Length == 1 && local[0].Body is { } body)
            {
                var returned = body.Statements.OfType<ReturnStatementSyntax>().LastOrDefault()?.Expression;
                return returned is not null && IsRunPath(returned, returned, declaration, [], factoryHops + 1);
            }
            return false;
        }
        if (expression is IdentifierNameSyntax identifier)
        {
            var name = identifier.Identifier.ValueText;
            if (!visited.Add(name)) return false;
            var method = site.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method is null) return false;
            var variable = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(node => node.Identifier.ValueText == name && node.SpanStart < site.SpanStart)
                .LastOrDefault();
            if (variable?.Initializer is null) return false;
            if (method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                    assignment.Left.ToString() == name && assignment.SpanStart < site.SpanStart)) return false;
            return IsRunPath(variable.Initializer.Value, variable, declaration, visited, factoryHops);
        }
        return false;
    }

    private static bool IsRelativeComponent(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => !Path.IsPathRooted(literal.Token.ValueText) &&
            !literal.Token.ValueText.Contains(':') && !literal.Token.ValueText.StartsWith('\\') &&
            !literal.Token.ValueText.Split('/', '\\').Contains(".."),
        InterpolatedStringExpressionSyntax interpolated =>
            interpolated.Contents.OfType<InterpolationSyntax>().All(item =>
                HasGuid(item.Expression) || item.Expression.ToString() is "slotIndex" or "buildSlot") &&
            interpolated.Contents.OfType<InterpolatedStringTextSyntax>().All(item =>
                !item.TextToken.ValueText.Contains('/') && !item.TextToken.ValueText.Contains('\\')),
        _ => HasGuid(expression)
    };

    private static bool HasGuid(SyntaxNode expression) =>
        expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Any(call => call.Expression.ToString() == "Guid.NewGuid");

    private static void VerifyIdentityHelpers(string repository)
    {
        RequireMethod(repository, "tests/Mcg.AgentOrchestrator.TestSupport/SharedTestSupport.cs", "CreateTempDirectory",
            "var path = Path.Combine(Path.GetTempPath(), \"mcg-orchestrator-tests\", Guid.NewGuid().ToString(\"n\"))", "return path");
        RequireMethod(repository, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs", "CreateManifestWorkspace",
            "var root = Path.Combine(Path.GetTempPath(), \"mcg-acceptance-tests\", Guid.NewGuid().ToString(\"N\"))", "return root");
        RequireMethod(repository, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierTests.cs", "CreateTrackedManifestShapeWorkspace",
            "var root = CreateManifestWorkspace", "return root");
        RequireMethod(repository, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierDotnetBuildSlotTests.cs",
            "CreateTwoLaneShardManifestWorkspace", "CreateManifestWorkspace");
        RequireMethod(repository, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestCollections.cs", "ClaimRoot",
            "Guid.NewGuid", "return (candidate, ownerLock)");
        var fixtureConstructor = Assert.Single(Parse(repository, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TestCollections.cs")
            .DescendantNodes().OfType<ConstructorDeclarationSyntax>(), node => node.ParameterList.Parameters.Count == 2);
        RequireTokens(fixtureConstructor, "(_root, _ownerLock) = ClaimRoot(basePath)", "IsolatedDotnetRootFixture");
        RequireTokens(fixtureConstructor,
            "Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root)",
            "IsolatedDotnetRootFixture");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Execution/Processes/DotnetBuildStorageLayout.cs", "CaptureStorageRoot",
            "Environment.GetEnvironmentVariable(IsolatedRootOverrideVariable)", "ResolveIsolatedRootBase");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Execution/Processes/DotnetBuildStorageLayout.cs", "ResolveIsolatedRootBase",
            "if (!string.IsNullOrWhiteSpace(overridden))", "return overridden");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs",
            "CaptureStorageRoot", "DotnetBuildStorageLayout.CaptureStorageRoot");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs",
            "TryAcquireFirstAvailableStableSlotExecutionLock", "storageRoot ??= CaptureStorageRoot()", "CreateStableSlotEnvironment(slot, storageRoot)");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs",
            "CreateStableSlotEnvironment", "BuildSlotExecutionLockPath(slotIndex % BuildConcurrencySlotCount, storageRoot)");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs",
            "BuildSlotExecutionLockPath", "Path.Combine(storageRoot.RootPath, \"build-slots\", $\"build-{slotIndex}.lock\")");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteExecutorOccupancy.cs",
            "Folder", "Path.Combine(root, \".orchestrator\", \"remote-executor-occupancy\", executor)");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Execution/Processes/OwnedProcessGroup.cs",
            "CreateKillOnCloseJob", "CreateJobObjectW(IntPtr.Zero, null)");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs",
            "ConfigureRegistry", "new SpawnRegistry(dbPath)", "Registry = registry");
        RequireMethod(repository, "src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.RegistryScope.cs",
            "UseRegistryScopeForTests", "ScopedRegistry.Value = new RegistryScopeState");
    }

    private static void RequireMethod(string repository, string path, string name, params string[] anchors)
    {
        var method = Assert.Single(Parse(repository, path).DescendantNodes().OfType<MethodDeclarationSyntax>(),
            node => node.Identifier.ValueText == name);
        foreach (var anchor in anchors)
            RequireTokens(method, anchor, $"{path}::{name}");
    }

    private static void RequireTokens(SyntaxNode source, string anchor, string location)
    {
        // Lexical tokens tolerate formatting while retaining arguments and return-expression meaning.
        var expected = CSharpSyntaxTree.ParseText(anchor).GetRoot().DescendantTokens()
            .Where(token => !token.IsMissing && !token.IsKind(SyntaxKind.EndOfFileToken))
            .Select(token => token.Text).ToArray();
        var actual = source.DescendantTokens().Select(token => token.Text).ToArray();
        Assert.NotEmpty(expected);
        Assert.True(Enumerable.Range(0, Math.Max(0, actual.Length - expected.Length + 1))
            .Any(start => actual.Skip(start).Take(expected.Length).SequenceEqual(expected)),
            $"Identity anchor changed: {location}: {anchor}.");
    }

    private static SyntaxNode Parse(string repository, string path)
    {
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(repository, path))).GetRoot();
        Assert.False(syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error), $"Cannot parse {path}.");
        return syntax;
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
