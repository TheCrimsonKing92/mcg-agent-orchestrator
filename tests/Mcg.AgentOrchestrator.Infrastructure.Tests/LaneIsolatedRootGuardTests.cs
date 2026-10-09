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
    public void Effective_lane_classes_keep_isolated_roots_or_named_exceptions() =>
        LaneIsolatedRootScanner.VerifyLane(VerifiedRepositoryRoot.Find(), "Dotnet build slots",
            typeof(LaneIsolatedRootGuardTests).Assembly, IsIsolatedCollection,
            CollectionExceptions, ChildClearExceptions, TestCollections.DotnetBuildSlots);

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
