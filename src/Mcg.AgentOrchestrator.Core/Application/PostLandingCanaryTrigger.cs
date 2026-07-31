namespace Mcg.AgentOrchestrator.Core;

public sealed record PostLandingCanaryTriggerResult(IReadOnlyList<string> TriggeringPaths)
{
    public bool ShouldRun => TriggeringPaths.Count > 0;
}

public static class PostLandingCanaryTrigger
{
    // Built-ins are intentionally not replaceable by configuration. The canary must still
    // notice a change that breaks the classifier or the configuration used by the gate.
    public static IReadOnlyList<string> EnginePathPrefixes { get; } =
    [
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier",
        "src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager",
        "src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGateEngineSettings",
        "config/acceptance-manifest.json"
    ];

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
