namespace Mcg.AgentOrchestrator.Core;

public sealed record PostLandingCanaryTriggerResult(IReadOnlyList<string> TriggeringPaths)
{
    public bool ShouldRun => TriggeringPaths.Count > 0;
}

public sealed record AcceptanceEngineOwnedSurface(
    string Name,
    IReadOnlyList<string> PathPrefixes);

public static class AcceptanceEngineSurfaceRegistry
{
    // This is the ownership contract for the acceptance engine. Adding a collaborator
    // requires extending this registry, which also makes it canary-triggering.
    public static IReadOnlyList<AcceptanceEngineOwnedSurface> Surfaces { get; } =
    [
        new("acceptance-verifier", [
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier",
            // Cleanup evidence is reached through the independently owned failure-cause adjudicator.
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceAssemblyCleanupEvidence",
            // Process helpers are reached through the independently owned acceptance process invoker.
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCommandProcessIdentityTracker",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceSdkConsoleRule",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/CaptureLimitStop",
            "src/Mcg.AgentOrchestrator.Execution/Processes/TempRootApparatusLossReceipts",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceSharedApparatusInvalidation",
            "src/Mcg.AgentOrchestrator.Execution/Processes/AcceptanceTempRootNames",
            "src/Mcg.AgentOrchestrator.Execution/Verification/AcceptanceTrxOutcomeTaxonomy",
            "src/Mcg.AgentOrchestrator.Execution/Verification/AcceptanceTrxTestIdentityResolver"
        ]),
        new("test-impact-planner", ["src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner"]),
        new("reverse-dependency-index", [
            "src/Mcg.AgentOrchestrator.Core/Application/ReverseDependencyTestImpactReader",
            "src/Mcg.AgentOrchestrator.Core/Application/TestClassDeclarationReader"
        ]),
        new("build-environment", [
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager",
            "src/Mcg.AgentOrchestrator.Execution/Processes/DotnetBuildStorageRoot",
            "src/Mcg.AgentOrchestrator.Execution/Processes/DotnetBuildStorageLayout"
        ]),
        new("change-classifier", ["src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier"]),
        new("gate-settings", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGateEngineSettings"]),
        new("test-coverage-invariant", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/TestCoverageInvariant"]),
        new("base-build-cache", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBaseBuildCache"]),
        new("gate-heartbeats", [
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateHeartbeatArtifacts",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateHeartbeatLockHolderProjection"
        ]),
        new("attempt-artifact-custody", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceAttemptArtifactCustody"]),
        new("acceptance-collaborators", [
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCheckCommandBuilder",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCheckProcessInvoker",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceContainedGenerationBaseline",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceFailureAttributionPlanner",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGateCancellationMonitor",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGatePhaseAccounting",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGitTextResolver",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceIdenticalTreeReuseRule",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceLaneDurationStore",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceLaneEarlyStop",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceLaneMembership",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceLaneRerunEvidence",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceLaneTestFailureRerun",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceManifestLocator",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceManifestPartitionEquivalence",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceOverlappedCheckRunner",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePartitionVerdictCache",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePartitionVerdictCache.MissReasons",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePartitionVerdictCache.RemoteLaneIdentity",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePolicyShardPlanner",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptancePolicyShardPlanner.CandidateTree",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceRetryEvidenceRetentionFailure",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceRunExecutionContext",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceRunExecutionContext.FollowerPinnedBase",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceStructuralCoverageEvaluator",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceStructuralCoveragePartitionPlan",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceTestInventorySource",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceTestSourceResolver",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceWithinAttemptRerunEvidence",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/CommandLineTooLongException",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DeclaredTestProjectInventory",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateChildReapSeam",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateHeartbeatContext",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateHeartbeatRunClass",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateHeartbeatRuntime",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateLoadContextProbe",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateShardLaneClass",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateShardPermitPool",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateShardPermitPool.Priority",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateShardWaiterMarkers",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/MainBaselineDiscoveryCache",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/MissingAcceptanceManifestException",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteExecutorHealthLedger",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteFocusedEvidenceShadow",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneCandidateIdentity",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneCoordinator",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneExecutor",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneExecutorConfiguration",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneFailureHistory",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneOfferHistoryReader",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneOfferPolicy",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SourceSizeRatchetPreflight",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/SshRemoteLaneExecutor",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/StateEffectProposals",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/StructuralCoverageFailureDetail",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/StructuralCoverageFailureMessage",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/StructuralCoveragePermitWait"
        ]),
        new("post-landing-canary", [
            "src/Mcg.AgentOrchestrator.App/Orchestration/PostLandingCanary",
            "src/Mcg.AgentOrchestrator.Core/Application/PostLandingCanaryTrigger",
            "tests/canary-fixture"
        ]),
        new("acceptance-manifest", ["config/acceptance-manifest.json"])
    ];
}

public static class PostLandingCanaryTrigger
{
    // Built-ins are intentionally not replaceable by configuration. The canary must still
    // notice a change that breaks the classifier or the configuration used by the gate.
    public static IReadOnlyList<string> EnginePathPrefixes { get; } =
        AcceptanceEngineSurfaceRegistry.Surfaces
            .SelectMany(surface => surface.PathPrefixes)
            .ToArray();

    public static PostLandingCanaryTriggerResult Evaluate(
        IEnumerable<string> changedFiles,
        IReadOnlyList<string>? additionalPathPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);

        var prefixes = EnginePathPrefixes
            .Concat(additionalPathPrefixes ?? [])
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var triggeringPaths = changedFiles
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Where(path => prefixes.Any(prefix => IsPathPrefix(prefix, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PostLandingCanaryTriggerResult(triggeringPaths);
    }

    private static bool IsPathPrefix(string prefix, string path) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(prefix.EndsWith('/') ? prefix : prefix + "/", StringComparison.OrdinalIgnoreCase) ||
        (!Path.HasExtension(prefix) &&
         path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
}
