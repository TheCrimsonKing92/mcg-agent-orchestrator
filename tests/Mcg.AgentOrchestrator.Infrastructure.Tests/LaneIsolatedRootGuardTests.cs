using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class LaneIsolatedRootGuardTests
{
    private const string ExplicitStorage =
        "Owns a GUID DotnetBuildStorageRoot via DotnetBuildEnvironmentManagerRootedTestBase; its separate collection protects process-local hooks.";

    // Waivers are named, reviewable, and rejected when no longer needed. They do not waive the clear scan.
    private static IReadOnlyDictionary<Type, string> LoadCollectionExceptions()
    {
        var exceptions = new Dictionary<Type, string>();
        foreach (var (name, reason) in TestCollectionGuardRules.LoadIsolatedCollectionExemptions(VerifiedRepositoryRoot.Find()))
        {
            var type = typeof(LaneIsolatedRootGuardTests).Assembly.GetType(name);
            Assert.True(type is not null, $"Stale collection exception: {name} is outside the lane.");
            exceptions.Add(type!, reason);
        }
        return exceptions;
    }

    private static readonly IReadOnlyDictionary<(Type Class, string Method), string> LocalHostChildClearExceptions =
        new Dictionary<(Type, string), string>
        {
            [(typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyFocusedRunner),
                nameof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyFocusedRunner.FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt))] =
                "The child removes its override to test fallback, then redirects LOCALAPPDATA to its GUID sharedProfileRoot. The parent rooted fixture is unchanged."
        };

    [Xunit.Fact]
    public void Effective_lane_classes_keep_isolated_roots_or_named_exceptions() =>
        LaneIsolatedRootScanner.VerifyLane(VerifiedRepositoryRoot.Find(), "Dotnet build slots",
            typeof(LaneIsolatedRootGuardTests).Assembly, IsIsolatedCollection,
            LoadCollectionExceptions(), new Dictionary<(Type, string), string>(), TestCollections.DotnetBuildSlots);

    [Xunit.Fact]
    public void Local_host_only_lane_keeps_isolated_roots_or_named_exceptions() =>
        LaneIsolatedRootScanner.VerifyLane(VerifiedRepositoryRoot.Find(), "Dotnet build slots local-only",
            typeof(LaneIsolatedRootGuardTests).Assembly, IsIsolatedCollection,
            new Dictionary<Type, string>
            {
                [typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyFocusedRunner)] =
                    "Owns explicit storage through the rooted base; its child uses a GUID LOCALAPPDATA fallback.",
                [typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyLockAttribution)] = ExplicitStorage,
                [typeof(DotnetBuildEnvironmentManagerTestsLocalHostOnlyHandleProbe)] = ExplicitStorage
            }, LocalHostChildClearExceptions, TestCollections.DotnetBuildSlots);

    private static bool IsIsolatedCollection(Type type) =>
        type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name == TestCollections.DotnetBuildSlots;

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
        Assert.Equal(violations, LaneIsolatedRootScanner.FindUnsafeClears(source).Count());
    }

    [Xunit.Theory]
    [Xunit.InlineData("FullyQualifiedName~NoSuchLaneGuardClass")]
    [Xunit.InlineData("Category!=Other")]
    [Xunit.InlineData("Name~SomeMethod")]
    [Xunit.InlineData("")]
    public void Filter_guard_rejects_missing_classes_and_unknown_syntax(string filter)
    {
        Assert.NotNull(Record.Exception(() => LaneIsolatedRootScanner.ValidateFilter(filter, [typeof(LaneIsolatedRootGuardTests)])));
    }
}
