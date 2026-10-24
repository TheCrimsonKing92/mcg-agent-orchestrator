namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class HermeticToolCacheEnvironment
{
    internal static void Apply(IDictionary<string, string?> environment, string profileRoot)
    {
            var powershellDirectory = Path.Combine(profileRoot, "powershell");
            Directory.CreateDirectory(powershellDirectory);
            environment["PSModuleAnalysisCachePath"] = Path.Combine(powershellDirectory, "ModuleAnalysisCache");
            GoToolCacheEnvironment.Apply(environment, profileRoot);
    }
}
