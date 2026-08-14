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
        string? modelName = null)
    {
        return new WorkerArtifactWriter().Write(
            goal,
            task,
            workingDirectory,
            preflightFindings,
            citedPriorEvidence,
            providerName,
            modelName);
    }

    public static IReadOnlyList<WorkerSkillRequirement> SelectSkillRequirements(
        Goal goal,
        TaskSpec task,
        string workingDirectory)
    {
        return new WorkerSkillSelector().SelectSkillRequirements(goal, task, workingDirectory);
    }
}
