using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerSkillRequirement(
    string Name,
    string RelativePath,
    string Usage,
    bool Available,
    string Reason);

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
        IClock? clock = null)
    {
        return new WorkerArtifactWriter().Write(
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
        string workingDirectory)
    {
        return new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
    }
}
