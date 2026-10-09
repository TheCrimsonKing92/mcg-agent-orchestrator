namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the ordered, read-only lookup of a selected skill, independently of routing.
internal sealed class WorkerSkillResolver(string? orchestratorSkillDirectory = null)
{
    internal WorkerSkillRequirement Resolve(
        string workingDirectory, string name, string relativePath, string usage, string reason)
    {
        var localPath = Path.GetFullPath(Path.Combine(workingDirectory, relativePath));
        var orchestratorPath = string.IsNullOrWhiteSpace(orchestratorSkillDirectory)
            ? null
            : Path.GetFullPath(Path.Combine(orchestratorSkillDirectory, name, "SKILL.md"));
        var source = File.Exists(localPath) ? WorkerSkillSource.WorkingDirectory
            : orchestratorPath is not null && File.Exists(orchestratorPath) ? WorkerSkillSource.Orchestrator
            : WorkerSkillSource.Missing;
        return new WorkerSkillRequirement(name, relativePath, usage, source, reason,
            localPath, orchestratorPath,
            source == WorkerSkillSource.WorkingDirectory ? localPath
                : source == WorkerSkillSource.Orchestrator ? orchestratorPath : null);
    }

    internal static string DescribeMissing(IEnumerable<WorkerSkillRequirement> missing) =>
        "blocked: missing required local skill(s): " +
        string.Join(", ", missing.Select(skill =>
            $"{skill.Name} at {skill.WorkingDirectoryCandidatePath}; orchestrator: " +
            (skill.OrchestratorCandidatePath ?? "no orchestrator skill directory was configured"))) +
        "; add the SKILL.md file(s) or adjust the task so the router no longer selects them";
}
