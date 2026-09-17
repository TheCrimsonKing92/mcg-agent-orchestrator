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
        new("acceptance-verifier", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier"]),
        new("test-impact-planner", ["src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner"]),
        new("reverse-dependency-index", [
            "src/Mcg.AgentOrchestrator.Core/Application/ReverseDependencyTestImpactReader",
            "src/Mcg.AgentOrchestrator.Core/Application/TestClassDeclarationReader"
        ]),
        new("build-environment", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager"]),
        new("change-classifier", ["src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier"]),
        new("gate-settings", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGateEngineSettings"]),
        new("test-coverage-invariant", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/TestCoverageInvariant"]),
        new("base-build-cache", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBaseBuildCache"]),
        new("gate-heartbeats", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GateHeartbeatArtifacts"]),
        new("attempt-artifact-custody", ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceAttemptArtifactCustody"]),
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
