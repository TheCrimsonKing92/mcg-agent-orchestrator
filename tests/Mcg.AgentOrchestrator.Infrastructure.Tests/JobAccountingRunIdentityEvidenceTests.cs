using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class JobAccountingRunIdentityEvidenceTests
{
    // Keyed by class AND site kind: an anonymous-job waiver never exempts that class's PID paths.
    private static readonly IReadOnlyDictionary<(Type Class, string Kind), string> Exceptions =
        new Dictionary<(Type, string), string>
        {
            [(typeof(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteShadowPreemption), "path")] =
                "arbiter.lock sits in RemoteExecutorSlotArbiter.Folder(f.Root, \"one\"), a subfolder of the GUID-derived per-run Fixture.Root (RemoteExecutorSlotArbiterTests.cs:83-85); IsRunPath does not trace calls into other methods."
        };

    [Xunit.Fact]
    public void Lane_job_pid_and_slot_sites_have_run_identity_or_live_named_exception()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var classes = new[] { "Goal acceptance verifier", "Goal acceptance build slots" }
            .SelectMany(lane => LaneIsolatedRootScanner.ResolveLaneClasses(repository, lane, GetType().Assembly))
            .Distinct().ToArray();
        var sources = AcceptanceTestClassSourcesMemo.Get(repository).ToDictionary(item => item.FullName);
        ClassDeclarationSyntax? ResolveFixture(string name)
        {
            var declarations = sources.Values.Where(source => name == source.FullName.Replace('+', '.') ||
                    name.StartsWith(source.FullName.Replace('+', '.') + ".", StringComparison.Ordinal))
                .SelectMany(source => source.SourcePaths).Distinct()
                .SelectMany(path => Parse(repository, path).DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Where(node => LaneIsolatedRootScanner.ClassName(node).Replace('+', '.') == name).ToArray();
            return declarations.Length == 1 ? declarations[0] : null;
        }
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
                    var proof = Proves(site, declaration, type, ResolveFixture);
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
    public void Path_evidence_rejects_fixed_temp_slot_lock_and_accepts_guid_rooted_slot_lock()
    {
        var fixedSyntax = CSharpSyntaxTree.ParseText(
            "class Probe { void Test() { var path = Path.Combine(Path.GetTempPath(), \"slot-1\", \"lease.lock\"); } }").GetRoot();
        var fixedDeclaration = Assert.Single(fixedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>());
        var fixedSite = Assert.Single(CreationSites(fixedDeclaration));
        Assert.Equal("path", fixedSite.Kind);
        Assert.NotNull(fixedSite.Identity);
        Assert.False(IsRunPath(fixedSite.Identity!, fixedSite.Node, fixedDeclaration));

        var rootedSyntax = CSharpSyntaxTree.ParseText(
            "class Probe { void Test() { var slotRoot = Path.Combine(Path.GetTempPath(), $\"x-{Guid.NewGuid():N}\"); " +
            "var path = Path.Combine(slotRoot, \"slot-1\", \"lease.lock\"); } }").GetRoot();
        var rootedDeclaration = Assert.Single(rootedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>());
        var rootedSite = Assert.Single(CreationSites(rootedDeclaration));
        Assert.Equal("path", rootedSite.Kind);
        Assert.NotNull(rootedSite.Identity);
        Assert.True(IsRunPath(rootedSite.Identity!, rootedSite.Node, rootedDeclaration));
    }

    [Xunit.Fact]
    public void Occupancy_evidence_accepts_factory_host_root_and_rejects_fixed_or_bare_roots()
    {
        var rootedSyntax = CSharpSyntaxTree.ParseText("""
            class Probe {
                sealed class Scenario {
                    internal string Root { get; }
                    internal string HostRoot => AcceptancePartitionVerdictCache.ResolveHostStateRoot(Root);
                    internal Scenario() { Root = CreateManifestWorkspace("{}"); }
                }
                void Test() {
                    var scenario = new Scenario();
                    using var claim = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one");
                }
            }
            """).GetRoot();
        var rootedDeclaration = Assert.Single(rootedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            node => node.Identifier.ValueText == "Probe");
        var rootedSite = Assert.Single(CreationSites(rootedDeclaration));
        Assert.Equal("occupancy", rootedSite.Kind);
        Assert.NotNull(rootedSite.Identity);
        Assert.True(Proves(rootedSite, rootedDeclaration, GetType()));

        var fixedSyntax = CSharpSyntaxTree.ParseText("""
            class Probe {
                sealed class Scenario {
                    internal string Root { get; }
                    internal string HostRoot => AcceptancePartitionVerdictCache.ResolveHostStateRoot(Root);
                    internal Scenario() { Root = Path.Combine(Path.GetTempPath(), "fixed"); }
                }
                void Test() {
                    var scenario = new Scenario();
                    using var claim = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one");
                }
            }
            """).GetRoot();
        var fixedDeclaration = Assert.Single(fixedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            node => node.Identifier.ValueText == "Probe");
        var fixedSite = Assert.Single(CreationSites(fixedDeclaration));
        Assert.Equal("occupancy", fixedSite.Kind);
        Assert.NotNull(fixedSite.Identity);
        Assert.False(Proves(fixedSite, fixedDeclaration, GetType()));

        var bareSyntax = CSharpSyntaxTree.ParseText("""
            class Probe {
                sealed class Scenario {
                    internal string Root { get; }
                    internal string HostRoot => AcceptancePartitionVerdictCache.ResolveHostStateRoot(Root);
                    internal Scenario() { Root = CreateManifestWorkspace("{}"); }
                    void Test() {
                        using var claim = RemoteExecutorOccupancy.TryClaimExclusive(Root, "one");
                    }
                }
            }
            """).GetRoot();
        var bareDeclaration = Assert.Single(bareSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            node => node.Identifier.ValueText == "Probe");
        var bareSite = Assert.Single(CreationSites(bareDeclaration));
        Assert.Equal("occupancy", bareSite.Kind);
        Assert.NotNull(bareSite.Identity);
        Assert.False(Proves(bareSite, bareDeclaration, GetType()));
    }

    // Proves the constructor-argument occupancy root only when every creation site passes a run-scoped path.
    [Xunit.Fact]
    public void Occupancy_evidence_accepts_constructor_assigned_root_only_when_every_creation_passes_a_run_path()
    {
        const string rootedSource = """
            class Probe {
                sealed class Scenario {
                    internal string Root { get; }
                    internal Scenario(Probe owner, string root) { Root = root; }
                    void Use() {
                        using var claim = RemoteExecutorOccupancy.TryClaimExclusive(Root, "one");
                    }
                }
                void Test() {
                    var root = CreateManifestWorkspace("{}");
                    var scenario = new Scenario(this, root);
                }
            }
            """;
        var rootedSyntax = CSharpSyntaxTree.ParseText(rootedSource).GetRoot();
        var rootedDeclaration = Assert.Single(rootedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            node => node.Identifier.ValueText == "Probe");
        var rootedSite = Assert.Single(CreationSites(rootedDeclaration));
        Assert.Equal("occupancy", rootedSite.Kind);
        Assert.NotNull(rootedSite.Identity);
        Assert.True(Proves(rootedSite, rootedDeclaration, GetType()));

        const string creation = "var scenario = new Scenario(this, root);";
        const string fixedCreation = "var fixedScenario = new Scenario(this, Path.Combine(Path.GetTempPath(), \"fixed\"));";
        var rejectedSources = new[]
        {
            rootedSource.Replace(creation, fixedCreation),
            rootedSource.Replace(creation, creation + fixedCreation),
            rootedSource.Replace("Root = root;", "Root = root; Root = Path.Combine(Path.GetTempPath(), \"fixed\");"),
            rootedSource.Replace(creation, "")
        };
        foreach (var source in rejectedSources)
        {
            var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
            var declaration = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                node => node.Identifier.ValueText == "Probe");
            var site = Assert.Single(CreationSites(declaration));
            Assert.Equal("occupancy", site.Kind);
            Assert.NotNull(site.Identity);
            Assert.False(Proves(site, declaration, GetType()));
        }
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

    // These probes prove the seam rule accepts job sites only for the three exact calls and only while
    // the lane scan anchors the invoker chain's release and dispose in finally. This proves handle
    // release and the absence of a fixed job name; it does not prove child exit.
    [Xunit.Fact]
    public void Job_evidence_accepts_only_exact_process_invoker_seams_and_rejects_other_job_creation()
    {
        var syntax = CSharpSyntaxTree.ParseText("""
            class Probe
            {
                void Test()
                {
                    GoalAcceptanceVerifier.RunProcessForTestsAsync(args);
                    GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync(args);
                    GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(args);
                }
            }
            """).GetRoot();
        var declaration = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>());
        var sites = CreationSites(declaration).ToArray();
        Assert.Equal(3, sites.Length);
        Assert.All(sites, site =>
        {
            Assert.Equal("job", site.Kind);
            Assert.True(Proves(site, declaration, GetType()));
        });

        foreach (var creation in new[]
                 {
                     "new OwnedProcessGroup();",
                     "GoalAcceptanceVerifier.RunProcessLeakyForTestsAsync(args);",
                     "CreateJobObjectW(IntPtr.Zero, null);",
                     "CreateKillOnCloseJob();",
                     "WorkerProcessJobs.TryRegister(process);"
                 })
        {
            var rejectedSyntax = CSharpSyntaxTree.ParseText("class Probe { void Test() { " + creation + " } }").GetRoot();
            var rejectedClass = Assert.Single(rejectedSyntax.DescendantNodes().OfType<ClassDeclarationSyntax>());
            var site = Assert.Single(CreationSites(rejectedClass));
            Assert.Equal("job", site.Kind);
            Assert.False(Proves(site, rejectedClass, GetType()));
        }
    }

    [Xunit.Fact]
    public void Process_invoker_chain_evidence_requires_dispose_in_finally()
    {
        const string invoker = """
            class AcceptanceCheckProcessInvoker
            {
                void RunAsync()
                {
                    RegisteredOwnedProcess? process = null;
                    var stdoutPipeName = $"capture-{Guid.NewGuid():N}-out";
                    var stderrPipeName = $"capture-{Guid.NewGuid():N}-err";
                    try { RunChild(); }
                    finally
                    {
                        try { process.Release(out accounting); }
                        finally { process.Dispose(); }
                    }
                }
            }
            """;
        const string processRunner = """
            class GoalAcceptanceVerifier
            {
                void RunProcessForTestsAsync() => RunProcessAsync();
                void RunProcessWithHeartbeatForTestsAsync() => RunProcessWithHeartbeatForTestsAsync(null);
                void RunProcessWithHeartbeatForTestsAsync(object observer) => RunProcessAsync();
                void RunProcessAsync() => RunProcessAsync(null);
                void RunProcessAsync(object observer) => AcceptanceCheckProcessInvoker.RunAsync();
            }
            """;
        const string captureLimit = """
            class GoalAcceptanceVerifier
            {
                void RunProcessWithCaptureLimitForTestsAsync() => RunProcessAsync();
            }
            """;
        const string workerProcessJobs = """
            class RegisteredOwnedProcess
            {
                void Dispose() => Release(out accounting);
            }
            """;
        const string ownedProcessGroup = """
            class OwnedProcessGroup
            {
                void Dispose() => _jobHandle?.Dispose();
            }
            """;

        Assert.True(ProcessInvokerChainReleasesInFinally(invoker, processRunner, captureLimit,
            workerProcessJobs, ownedProcessGroup, out var failure), failure);
        Assert.False(ProcessInvokerChainReleasesInFinally(invoker.Replace("process.Dispose();", ""),
            processRunner, captureLimit, workerProcessJobs, ownedProcessGroup, out _));
        var disposeInTry = invoker.Replace("process.Dispose();", "")
            .Replace("try { RunChild(); }", "try { RunChild(); process.Dispose(); }");
        Assert.False(ProcessInvokerChainReleasesInFinally(disposeInTry, processRunner, captureLimit,
            workerProcessJobs, ownedProcessGroup, out _));
    }

    [Xunit.Fact]
    public void Local_fixture_root_evidence_requires_immutable_guid_root_and_unreassigned_local()
    {
        const string fixtureSource = """
            class External
            {
                public class Fixture
                {
                    public string Root { get; } = Path.Combine(Path.GetTempPath(), "x-" + Guid.NewGuid().ToString("N"));
                    public Fixture() { }
                }
            }
            """;
        const string caller = """
            using Fixture = External.Fixture;
            class Probe
            {
                void Test()
                {
                    var f = new Fixture();
                    RemoteExecutorOccupancy.Claim(f.Root);
                }
            }
            """;
        static bool ProvesFixture(string source, string fixture)
        {
            var (site, declaration) = OccupancyProbe(source);
            var syntax = CSharpSyntaxTree.ParseText(fixture).GetRoot();
            Assert.DoesNotContain(syntax.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            var external = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
                node => node.Identifier.ValueText == "Fixture");
            return IsLocalFixtureRunScopedRoot(site, declaration, external);
        }
        Assert.True(ProvesFixture(caller, fixtureSource));
        Assert.True(ProvesFixture(caller.Replace("new Fixture()", "new External.Fixture()"), fixtureSource));
        foreach (var rejected in new[]
                 {
                     fixtureSource.Replace("\"x-\" + Guid.NewGuid().ToString(\"N\")", "\"shared\""),
                     fixtureSource.Replace("{ get; }", "{ get; set; }"),
                     fixtureSource.Replace("{ get; }", "{ get; init; }"),
                     fixtureSource.Replace("public Fixture() { }", "public Fixture() { Root = Path.GetTempPath(); }"),
                     fixtureSource.Replace("public Fixture() { }",
                         "public Fixture() { int x; (Root, x) = (Path.GetTempPath(), 0); }"),
                     fixtureSource.Replace("public class Fixture", "public partial class Fixture")
                 })
            Assert.False(ProvesFixture(caller, rejected), rejected);
        Assert.False(ProvesFixture(caller.Replace("void Test()", "void Test(Fixture f)")
            .Replace("var f = new Fixture();", ""), fixtureSource));
        Assert.False(ProvesFixture(caller.Replace("RemoteExecutorOccupancy.Claim(f.Root);",
            "RemoteExecutorOccupancy.Claim(f.Root); f = new Fixture();"), fixtureSource));
        Assert.False(ProvesFixture(caller.Replace("using Fixture = External.Fixture;", "using Fixture = Other.Fixture;"),
            fixtureSource));
        Assert.False(ProvesFixture(caller.Replace("void Test()",
            "class Fixture { public string Root { get; } = Path.Combine(Path.GetTempPath(), \"shared\"); } void Test()"),
            fixtureSource));
        Assert.False(ProvesFixture(caller.Replace("class Probe",
            "class Base { public class Fixture { public string Root { get; } = Path.GetTempPath(); } } class Probe : Base"),
            fixtureSource));
        Assert.False(ProvesFixture(caller.Replace("var f = new Fixture();",
            "var f = new Fixture(); ref var alias = ref f; alias = new Fixture();"), fixtureSource));
        Assert.False(ProvesFixture(caller.Replace("var f = new Fixture();",
            "var f = new Fixture(); ref var alias = ref (f); alias = new Fixture();"), fixtureSource));
        Assert.False(ProvesFixture(caller.Replace("using Fixture = External.Fixture;", "")
            .Replace("class Probe", """
                class Fixture { public string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); }
                class Base { public class Fixture { public string Root { get; } = Path.GetTempPath(); } }
                class Probe : Base
                """), fixtureSource));
        foreach (var claim in new[]
                 {
                     "Action<Fixture> action = f => RemoteExecutorOccupancy.Claim(f.Root);",
                     "void Claim(Fixture f) { RemoteExecutorOccupancy.Claim(f.Root); } Claim(f);"
                 })
            Assert.False(ProvesFixture(caller.Replace("RemoteExecutorOccupancy.Claim(f.Root);", claim), fixtureSource));
    }

    [Xunit.Fact]
    public void Nested_constructor_root_evidence_requires_named_factory_and_unreassigned_local()
    {
        const string source = """
            class Probe
            {
                void Test()
                {
                    var s = new JobAccountingScenario();
                    RemoteExecutorOccupancy.Claim(s.Root);
                }
                class JobAccountingScenario
                {
                    public string Root { get; }
                    public JobAccountingScenario() { Root = CreateManifestWorkspace("manifest"); }
                    void Other() { }
                }
            }
            """;
        static bool ProvesNested(string text)
        {
            var (site, declaration) = OccupancyProbe(text);
            return IsNestedConstructorRunScopedRoot(site, declaration);
        }
        Assert.True(ProvesNested(source));
        foreach (var rejected in new[]
                 {
                     source.Replace("CreateManifestWorkspace(\"manifest\")", "Path.GetTempPath()"),
                     source.Replace("void Other() { }", "void Other() { Root = CreateManifestWorkspace(\"other\"); }"),
                     source.Replace("void Other() { }",
                         "void Other() { int x; (Root, x) = (CreateManifestWorkspace(\"other\"), 0); }"),
                     source.Replace("{ get; }", "{ get; set; }"),
                     source.Replace("public string Root { get; }",
                         "public string Root { get; } = CreateManifestWorkspace(\"init\");"),
                     source.Replace("Root = CreateManifestWorkspace", "Root ??= CreateManifestWorkspace"),
                     source.Replace("class JobAccountingScenario", "partial class JobAccountingScenario"),
                     source.Replace("void Test()", "void Test(JobAccountingScenario s)")
                         .Replace("var s = new JobAccountingScenario();", ""),
                     source.Replace("RemoteExecutorOccupancy.Claim(s.Root);",
                         "Action<JobAccountingScenario> action = s => RemoteExecutorOccupancy.Claim(s.Root);"),
                     source.Replace("var s = new JobAccountingScenario();",
                         "var s = new JobAccountingScenario(); ref var alias = ref s; alias = new JobAccountingScenario();"),
                     source.Replace("var s = new JobAccountingScenario();",
                         "var s = new JobAccountingScenario(); ref var alias = ref (s); alias = new JobAccountingScenario();"),
                     source.Replace("RemoteExecutorOccupancy.Claim(s.Root);",
                         "RemoteExecutorOccupancy.Claim(s.Root); s = new JobAccountingScenario();")
                 })
            Assert.False(ProvesNested(rejected), rejected);
        Assert.False(ProvesNested("""
            class Probe
            {
                class JobAccountingScenario
                {
                    public string Root { get; }
                    public JobAccountingScenario() { Root = CreateManifestWorkspace("manifest"); }
                }
                class Other
                {
                    void Test() { var s = new JobAccountingScenario(); RemoteExecutorOccupancy.Claim(s.Root); }
                    class JobAccountingScenario { public string Root { get; } = Path.GetTempPath(); }
                }
            }
            """));
    }

    private static (Site Site, ClassDeclarationSyntax Declaration) OccupancyProbe(string source)
    {
        var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
        Assert.DoesNotContain(syntax.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var declaration = Assert.Single(syntax.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            node => node.Identifier.ValueText == "Probe");
        return (Assert.Single(CreationSites(declaration)), declaration);
    }

    private sealed record Site(string Kind, SyntaxNode Node, ExpressionSyntax? Identity = null);

    private static bool Proves(Site site, ClassDeclarationSyntax declaration, Type type,
        Func<string, ClassDeclarationSyntax?>? resolveFixture = null) => site.Kind switch
    {
        "slot" => UsesFixtureStorage(site.Node, type),
        "registry" or "path" => site.Identity is not null &&
            IsRunPath(site.Identity, site.Node, declaration),
        "dispatch" => IsRegistryScoped(site, declaration),
        "job" => IsRegistryScoped(site, declaration) || IsOwnedByProcessInvokerSeam(site.Node),
        "occupancy" => site.Identity is not null &&
            (IsScenarioHostRoot(site.Identity, declaration) || IsScenarioConstructorRunScopedRoot(site, declaration) ||
             IsLocalFixtureRunScopedRoot(site, declaration, resolveFixture?.Invoke(LocalFixtureTypeName(site))) ||
             IsNestedConstructorRunScopedRoot(site, declaration)),
        _ => false
    };

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

    private static bool IsOwnedByProcessInvokerSeam(SyntaxNode node) =>
        node is InvocationExpressionSyntax call && call.Expression.ToString() is
            "GoalAcceptanceVerifier.RunProcessForTestsAsync" or
            "GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync" or
            "GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync";

    private static bool IsScenarioHostRoot(ExpressionSyntax identity, ClassDeclarationSyntax declaration)
    {
        if (identity is not MemberAccessExpressionSyntax member || member.Name.Identifier.ValueText != "HostRoot")
            return false;
        var scenarios = declaration.Members.OfType<ClassDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText == "Scenario").ToArray();
        if (scenarios.Length != 1) return false;
        var scenario = scenarios[0];
        if (!scenario.Members.OfType<PropertyDeclarationSyntax>().Any(property =>
                property.Identifier.ValueText == "HostRoot" && property.ExpressionBody is { } body &&
                SyntaxFactory.AreEquivalent(body.Expression,
                    SyntaxFactory.ParseExpression("AcceptancePartitionVerdictCache.ResolveHostStateRoot(Root)"))))
            return false;
        var assignments = scenario.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is IdentifierNameSyntax name && name.Identifier.ValueText == "Root")
            .ToArray();
        return assignments.Any(assignment => assignment.Ancestors().OfType<ConstructorDeclarationSyntax>()
                   .Any(constructor => constructor.Parent == scenario)) &&
               assignments.All(assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                   assignment.Right is InvocationExpressionSyntax call && IsNamedRunFactory(call.Expression.ToString()));
    }

    private static bool IsScenarioConstructorRunScopedRoot(Site site, ClassDeclarationSyntax declaration)
    {
        if (site.Identity is not IdentifierNameSyntax identity || identity.Identifier.ValueText != "Root")
            return false;
        var scenario = site.Node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (scenario is null || scenario.Identifier.ValueText != "Scenario" || scenario.Parent != declaration ||
            declaration.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Count(node => node.Identifier.ValueText == "Scenario") != 1)
            return false;
        var constructors = scenario.Members.OfType<ConstructorDeclarationSyntax>().ToArray();
        if (constructors.Length != 1 || constructors[0].Body is not { } body ||
            scenario.DescendantNodes().OfType<PropertyDeclarationSyntax>().Any(property =>
                property.Identifier.ValueText == "Root" &&
                (property.Initializer is not null || property.ExpressionBody is not null)))
            return false;
        var assignments = scenario.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is IdentifierNameSyntax name && name.Identifier.ValueText == "Root" ||
                assignment.Left is MemberAccessExpressionSyntax member && member.Expression is ThisExpressionSyntax &&
                member.Name.Identifier.ValueText == "Root").ToArray();
        if (assignments.Length != 1 || !assignments[0].IsKind(SyntaxKind.SimpleAssignmentExpression) ||
            assignments[0].Left is not IdentifierNameSyntax || !body.Span.Contains(assignments[0].Span) ||
            assignments[0].Right is not IdentifierNameSyntax source)
            return false;
        var parameters = constructors[0].ParameterList.Parameters;
        var parameter = parameters.FirstOrDefault(node => node.Identifier.ValueText == source.Identifier.ValueText);
        if (parameter?.Type is not PredefinedTypeSyntax parameterType ||
            !parameterType.Keyword.IsKind(SyntaxKind.StringKeyword) ||
            body.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                assignment.Left is IdentifierNameSyntax name && name.Identifier.ValueText == source.Identifier.ValueText))
            return false;
        var parameterIndex = parameters.IndexOf(parameter);
        var creations = declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.ToString().Split('.').Last() == "Scenario").ToArray();
        return creations.Length > 0 && creations.All(creation =>
            creation.ArgumentList is { } arguments && arguments.Arguments.Count > parameterIndex &&
            arguments.Arguments.Take(parameterIndex + 1).All(argument => argument.NameColon is null &&
                argument.RefKindKeyword.IsKind(SyntaxKind.None)) &&
            IsRunPath(arguments.Arguments[parameterIndex].Expression, creation, declaration));
    }

    // Forms X and Y consume syntax only; the live scan supplies an external declaration through the source memo.
    private static bool IsLocalFixtureRunScopedRoot(Site site, ClassDeclarationSyntax declaration,
        ClassDeclarationSyntax? externalFixture = null)
    {
        var creation = LocalRootCreation(site);
        if (creation?.ArgumentList?.Arguments.Count != 0) return false;
        var name = LocalFixtureTypeName(site);
        var candidates = declaration.SyntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Concat(externalFixture is null ? [] : new[] { externalFixture })
            .Where(node => LaneIsolatedRootScanner.ClassName(node).Replace('+', '.') == name ||
                node.Parent == declaration && node.Identifier.ValueText == name).Distinct().ToArray();
        if (candidates.Length != 1) return false;
        var fixture = candidates[0];
        var root = GetOnlyRoot(fixture);
        return root?.Initializer?.Value is InvocationExpressionSyntax call &&
               call.Expression.ToString() == "Path.Combine" &&
               call.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == "Path.GetTempPath()" &&
               !RootAssignments(fixture).Any() && IsRunPath(call, root, fixture);
    }

    private static bool IsNestedConstructorRunScopedRoot(Site site, ClassDeclarationSyntax declaration)
    {
        if (site.Node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault() != declaration ||
            LocalRootCreation(site) is not { } creation) return false;
        var name = creation.Type.ToString();
        var candidates = declaration.Members.OfType<ClassDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText == name).ToArray();
        if (candidates.Length != 1) return false;
        var nested = candidates[0];
        if (GetOnlyRoot(nested) is not { Initializer: null }) return false;
        var assignments = RootAssignments(nested).ToArray();
        return assignments.Length > 0 && assignments.All(assignment =>
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            assignment.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Parent == nested &&
            !assignment.Ancestors().Any(node => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax or
                AnonymousFunctionExpressionSyntax) &&
            assignment.Right is InvocationExpressionSyntax call && IsNamedRunFactory(call.Expression.ToString()));
    }

    private static ObjectCreationExpressionSyntax? LocalRootCreation(Site site)
    {
        if (site.Identity is not MemberAccessExpressionSyntax member || member.Name.Identifier.ValueText != "Root" ||
            member.Expression is not IdentifierNameSyntax receiver ||
            site.Node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { } method)
            return null;
        var name = receiver.Identifier.ValueText;
        // A lambda/local function has its own receiver bindings, even when it is textually inside this method.
        if (site.Node.Ancestors().TakeWhile(node => node != method)
            .Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)) return null;
        var locals = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(node => node.Identifier.ValueText == name).ToArray();
        if (locals.Length != 1 || locals[0].Parent?.Parent is not LocalDeclarationStatementSyntax ||
            locals[0].SpanStart >= site.Node.SpanStart ||
            locals[0].Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() != method ||
            locals[0].Ancestors().OfType<BlockSyntax>().FirstOrDefault()?.Span.Contains(site.Node.Span) != true ||
            method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(assignment => assignment.Left.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Any(node => node.Identifier.ValueText == name)) ||
            method.DescendantNodes().OfType<ArgumentSyntax>().Any(argument =>
                !argument.RefKindKeyword.IsKind(SyntaxKind.None) && argument.Expression.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>().Any(node => node.Identifier.ValueText == name)) ||
            method.DescendantNodes().OfType<RefExpressionSyntax>().Any(reference => reference.Expression.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>().Any(node => node.Identifier.ValueText == name)))
            return null;
        return locals[0].Initializer?.Value is ObjectCreationExpressionSyntax { Initializer: null } creation
            ? creation : null;
    }

    private static string LocalFixtureTypeName(Site site)
    {
        if (LocalRootCreation(site) is not { } creation) return "";
        // Without semantic base-type resolution, inherited nested types could hide aliases or ordinary names.
        if (creation.Ancestors().OfType<ClassDeclarationSyntax>().Any(scope => scope.BaseList is not null)) return "";
        var name = creation.Type.ToString();
        var first = name.Split('.')[0];
        // Nested types hide file aliases; prove the class actually constructed, not the alias it shadows.
        foreach (var scope in creation.Ancestors().OfType<ClassDeclarationSyntax>())
        {
            var nested = scope.Members.OfType<ClassDeclarationSyntax>()
                .Where(node => node.Identifier.ValueText == first).ToArray();
            if (nested.Length > 0)
                return nested.Length == 1
                    ? LaneIsolatedRootScanner.ClassName(nested[0]).Replace('+', '.') + name[first.Length..] : "";
        }
        var aliases = site.Node.SyntaxTree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(node => node.Alias?.Name.Identifier.ValueText == first).ToArray();
        return aliases.Length == 1 ? aliases[0].Name + name[first.Length..] : name;
    }

    private static PropertyDeclarationSyntax? GetOnlyRoot(ClassDeclarationSyntax declaration)
    {
        var roots = declaration.Members.OfType<PropertyDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText == "Root").ToArray();
        // A single declaration cannot prove assignments in unseen partial declarations.
        return !declaration.Modifiers.Any(SyntaxKind.PartialKeyword) &&
               roots.Length == 1 && !roots[0].Modifiers.Any(SyntaxKind.StaticKeyword) &&
               roots[0].ExpressionBody is null && roots[0].AccessorList?.Accessors is { Count: 1 } accessors &&
               accessors[0].IsKind(SyntaxKind.GetAccessorDeclaration) && accessors[0].Body is null &&
               accessors[0].ExpressionBody is null && accessors[0].SemicolonToken.IsKind(SyntaxKind.SemicolonToken)
            ? roots[0] : null;
    }

    private static IEnumerable<AssignmentExpressionSyntax> RootAssignments(ClassDeclarationSyntax declaration) =>
        declaration.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(assignment =>
            assignment.Left.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(name => name.Identifier.ValueText == "Root"));

    private static bool IsNamedRunFactory(string name) => name.Split('.').Last() is
        "CreateManifestWorkspace" or "CreateTrackedManifestShapeWorkspace" or "CreateTwoLaneShardManifestWorkspace";

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
            if (IsNamedRunFactory(name)) return true; // Named scenario constructors checked below.
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
        Assert.True(ProcessInvokerChainReleasesInFinally(
            File.ReadAllText(Path.Combine(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCheckProcessInvoker.cs")),
            File.ReadAllText(Path.Combine(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.ProcessRunner.cs")),
            File.ReadAllText(Path.Combine(repository, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.CaptureLimit.cs")),
            File.ReadAllText(Path.Combine(repository, "src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs")),
            File.ReadAllText(Path.Combine(repository, "src/Mcg.AgentOrchestrator.Execution/Processes/OwnedProcessGroup.cs")),
            out var failure), failure);
    }

    private static bool ProcessInvokerChainReleasesInFinally(string invoker, string processRunner,
        string captureLimit, string workerProcessJobs, string ownedProcessGroup, out string? failure)
    {
        var sources = new[]
        {
            (Name: "AcceptanceCheckProcessInvoker", Text: invoker),
            (Name: "GoalAcceptanceVerifier.ProcessRunner", Text: processRunner),
            (Name: "GoalAcceptanceVerifier.CaptureLimit", Text: captureLimit),
            (Name: "RegisteredOwnedProcess", Text: workerProcessJobs),
            (Name: "OwnedProcessGroup", Text: ownedProcessGroup)
        };
        var roots = sources.Select(source => CSharpSyntaxTree.ParseText(source.Text).GetRoot()).ToArray();
        for (var index = 0; index < roots.Length; index++)
        {
            if (!roots[index].GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error)) continue;
            failure = $"Cannot parse process ownership source: {sources[index].Name}.";
            return false;
        }

        static IEnumerable<MethodDeclarationSyntax> Methods(SyntaxNode root, string className, string methodName) =>
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(node => node.Identifier.ValueText == className)
                .SelectMany(node => node.Members.OfType<MethodDeclarationSyntax>())
                .Where(node => node.Identifier.ValueText == methodName);

        var checks = new[]
        {
            (Name: "GoalAcceptanceVerifier.RunProcessForTestsAsync -> RunProcessAsync",
                Passed: Methods(roots[1], "GoalAcceptanceVerifier", "RunProcessForTestsAsync")
                    .Any(method => HasTokens(method, "RunProcessAsync("))),
            (Name: "GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync -> RunProcessAsync",
                Passed: Methods(roots[1], "GoalAcceptanceVerifier", "RunProcessWithHeartbeatForTestsAsync")
                    .Any(method => HasTokens(method, "RunProcessAsync("))),
            (Name: "GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync -> RunProcessAsync",
                Passed: Methods(roots[2], "GoalAcceptanceVerifier", "RunProcessWithCaptureLimitForTestsAsync")
                    .Any(method => HasTokens(method, "RunProcessAsync("))),
            (Name: "GoalAcceptanceVerifier.RunProcessAsync -> AcceptanceCheckProcessInvoker.RunAsync",
                Passed: Methods(roots[1], "GoalAcceptanceVerifier", "RunProcessAsync")
                    .Any(method => HasTokens(method, "AcceptanceCheckProcessInvoker.RunAsync("))),
            (Name: "AcceptanceCheckProcessInvoker.RunAsync: owned process, GUID capture pipes, finally release/dispose",
                Passed: Methods(roots[0], "AcceptanceCheckProcessInvoker", "RunAsync").Any(method =>
                    method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                        .Any(local => HasTokens(local, "RegisteredOwnedProcess? process")) &&
                    new[] { "stdoutPipeName", "stderrPipeName" }.All(name =>
                        method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(variable =>
                            variable.Identifier.ValueText == name && variable.Initializer is { } initializer &&
                            HasGuid(initializer.Value))) &&
                    method.DescendantNodes().OfType<FinallyClauseSyntax>().Any(clause =>
                        clause.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                            call.Expression.ToString() == "process.Release" &&
                            call.ArgumentList.Arguments.FirstOrDefault()?.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) == true)) &&
                    method.DescendantNodes().OfType<FinallyClauseSyntax>().Any(clause =>
                        clause.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call =>
                            call.Expression.ToString() == "process.Dispose" && call.ArgumentList.Arguments.Count == 0)))),
            (Name: "RegisteredOwnedProcess.Dispose -> Release(out",
                Passed: Methods(roots[3], "RegisteredOwnedProcess", "Dispose")
                    .Any(method => HasTokens(method, "Release(out"))),
            (Name: "OwnedProcessGroup.Dispose -> _jobHandle?.Dispose()",
                Passed: Methods(roots[4], "OwnedProcessGroup", "Dispose")
                    .Any(method => HasTokens(method, "_jobHandle?.Dispose()")))
        };
        foreach (var check in checks)
        {
            if (check.Passed) continue;
            failure = $"Process ownership anchor changed: {check.Name}.";
            return false;
        }
        failure = null;
        return true;
    }

    private static bool HasTokens(SyntaxNode source, string anchor)
    {
        var expected = CSharpSyntaxTree.ParseText(anchor).GetRoot().DescendantTokens()
            .Where(token => !token.IsMissing && !token.IsKind(SyntaxKind.EndOfFileToken))
            .Select(token => token.Text).ToArray();
        var actual = source.DescendantTokens().Select(token => token.Text).ToArray();
        return expected.Length > 0 && Enumerable.Range(0, Math.Max(0, actual.Length - expected.Length + 1))
            .Any(start => actual.Skip(start).Take(expected.Length).SequenceEqual(expected));
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
