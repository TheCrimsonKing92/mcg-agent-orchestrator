using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    internal ClaudeTranscriptUsageReader ClaudeTranscriptUsage { get; init; } = ClaudeTranscriptUsageReader.CreateDefault();

    internal ClaudeTranscriptUsageResult ResolveDispatchProviderUsage(TaskDispatchRecord? dispatch, string standardOutputPath)
    {
        var codex = NormalizeStructuredCodexOutput(dispatch, standardOutputPath);
        if (codex is not null)
            return new(codex.Usage, codex.UsageUnavailableReason);

        if (dispatch?.WorkerProviderKind == ProviderKind.AnthropicClaudeCli)
        {
            IReadOnlyList<string>? additionalRoots = dispatch.SandboxLowIntegrity
                ? [Path.Combine(dispatch.WorkingDirectory, ".mcg-sandbox")]
                : null;
            return ClaudeTranscriptUsage.Read(dispatch.ProviderSessionId, additionalRoots);
        }

        return new(null, "unsupported");
    }

    private static DateTimeOffset? ReceiptlessUsageAttemptAt(TaskDispatchRecord? dispatch) =>
        dispatch?.ContextPackageReceipt is null ? dispatch?.DispatchedAt : null;

    private static TaskSpec GetTaskAfterReceiptlessUsage(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, DispatchRefreshOutcome outcome)
    {
        var task = kernel.GetTask(goalId, taskId);
        if (outcome.Verification is not null && outcome.ReceiptlessUsageAttemptAt is { } attemptAt)
        {
            var dispatch = task.DispatchHistory.SingleOrDefault(candidate => candidate.DispatchedAt == attemptAt)
                ?? throw new InvalidOperationException($"Cannot record provider usage for unknown dispatch attempt {attemptAt:O}.");
            if (dispatch.ContextPackageReceipt is null)
                kernel.RecordDispatchProviderUsage(goalId, taskId, attemptAt,
                    DispatchProviderUsage.From(outcome.ProviderUsage, outcome.ProviderUsageUnavailableReason));
        }
        return kernel.GetTask(goalId, taskId);
    }
}
