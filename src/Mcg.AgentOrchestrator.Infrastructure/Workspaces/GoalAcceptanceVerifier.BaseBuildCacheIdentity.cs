namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    // The verifier adapts its process runner, execution permit and hermetic environment
    // to the build phase's identity hook; the build phase has no verifier dependency.
    private static async Task<DotnetBaseBuildCacheIdentity?> ResolveBaseBuildCacheIdentityAsync(
        DotnetTestBuildPhase phase, DotnetBuildEnvironment environment, string worktreePath,
        Func<IDisposable?> acquirePermit,
        Func<string[], Task<CommandResult>> run,
        Func<DotnetBaseBuildCacheIdentity?>? resolveForTests)
    {
        if (resolveForTests is not null) return resolveForTests();
        using var permit = acquirePermit();
        var processEnvironment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
        ConfigureHermeticVerificationEnvironment(processEnvironment, worktreePath, environment.RootPath);
        return await DotnetBaseBuildCacheIdentityResolver.ResolveAsync(
            worktreePath, phase.CachePlan!.RestoreProjects, phase.BuildArguments, environment, run,
            processEnvironment.ToDictionary(entry => entry.Key, entry => entry.Value ?? "")).ConfigureAwait(false);
    }
}
