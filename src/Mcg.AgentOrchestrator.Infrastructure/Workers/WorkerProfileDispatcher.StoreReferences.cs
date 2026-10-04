using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProfileDispatcher
{
    private static string WriteDispatchContextArtifacts(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string workingDirectory, IReadOnlyList<string>? preflightFindings, string? providerName, string? modelName,
        CitedPriorEvidenceResolver? resolver, DateTimeOffset dispatchedAt)
    {
        var answers = kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && request.Kind == HumanWaitKind.PlannerPrerequisiteEvidence
                && request.IsCompleted && !request.WasDismissed && request.SupersededByRequestId is null
                && !request.IsSyntheticParkedHumanWaitCompletion && !string.IsNullOrWhiteSpace(request.Answer))
            .OrderBy(request => request.AnsweredAt)
            .ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .Select(request => request.Answer!).ToArray();
        return WorkerContextArtifacts.Write(goal, task, workingDirectory, preflightFindings,
            resolver?.Resolve(goal, task), providerName, modelName,
            orchestratorStoreRoot: resolver?.OrchestratorDirectory,
            answeredEvidenceTexts: answers, clock: new DispatchContextClock(dispatchedAt));
    }

    private sealed class DispatchContextClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
