using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum WorkerSkillSource { WorkingDirectory, Orchestrator, Missing }

public sealed record WorkerSkillRequirement(
    string Name,
    string RelativePath,
    string Usage,
    WorkerSkillSource ResolvedSource,
    string Reason,
    string WorkingDirectoryCandidatePath,
    string? OrchestratorCandidatePath,
    string? ResolvedPath)
{
    public bool Available => ResolvedSource != WorkerSkillSource.Missing;
}

public static class WorkerContextArtifacts
{
    public static string Write(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        IReadOnlyList<string>? preflightFindings = null,
        string? citedPriorEvidence = null,
        string? providerName = null,
        string? modelName = null,
        string? orchestratorStoreRoot = null,
        IReadOnlyList<string>? answeredEvidenceTexts = null,
        IClock? clock = null,
        string? orchestratorSkillDirectory = null)
    {
        return new WorkerArtifactWriter(orchestratorSkillDirectory).Write(
            goal,
            task,
            workingDirectory,
            preflightFindings,
            citedPriorEvidence,
            providerName,
            modelName,
            orchestratorStoreRoot,
            answeredEvidenceTexts,
            clock);
    }

    public static IReadOnlyList<WorkerSkillRequirement> SelectSkillRequirements(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        string? orchestratorSkillDirectory = null)
    {
        return new WorkerSkillSelector(new WorkerSkillResolver(orchestratorSkillDirectory))
            .SelectSkillRequirements(goal, task, workingDirectory);
    }
}
