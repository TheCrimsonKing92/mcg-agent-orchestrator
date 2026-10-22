using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProfileDispatcher
{
    private static string WriteDispatchContextArtifacts(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string workingDirectory, IReadOnlyList<string>? preflightFindings, string? providerName, string? modelName,
        CitedPriorEvidenceResolver? resolver, DateTimeOffset dispatchedAt, string? orchestratorSkillDirectory, WorkerTargetHome? targetHome = null)
    {
        var prerequisiteAnswers = kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && request.Kind == HumanWaitKind.PlannerPrerequisiteEvidence
                && request.IsCompleted && !request.WasDismissed && request.SupersededByRequestId is null
                && !request.IsSyntheticParkedHumanWaitCompletion && !string.IsNullOrWhiteSpace(request.Answer))
            .OrderBy(request => request.AnsweredAt)
            .ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .Select(request => request.Answer!).ToArray();
        // Prerequisite evidence keeps its existing priority within the shared resolver budgets.
        var clarificationAnswers = kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && request.TaskId is not null && request.TaskId == task.Id
                && request.Kind == HumanWaitKind.SpecClarification
                && request.IsCompleted && !request.WasDismissed && request.SupersededByRequestId is null
                && !request.IsSyntheticParkedHumanWaitCompletion
                && !string.IsNullOrWhiteSpace(request.AuthoritativeAnswer?.Text))
            .OrderBy(request => request.AuthoritativeAnswer!.AnsweredAt)
            .ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .Select(request => request.AuthoritativeAnswer!.Text);
        return WorkerContextArtifacts.Write(goal, task, workingDirectory, preflightFindings,
            resolver?.Resolve(goal, task), providerName, modelName,
            orchestratorStoreRoot: resolver?.OrchestratorDirectory,
            answeredEvidenceTexts: prerequisiteAnswers.Concat(clarificationAnswers).ToArray(), clock: new DispatchContextClock(dispatchedAt),
            orchestratorSkillDirectory: orchestratorSkillDirectory, targetHome: targetHome);
    }

    private sealed class DispatchContextClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
