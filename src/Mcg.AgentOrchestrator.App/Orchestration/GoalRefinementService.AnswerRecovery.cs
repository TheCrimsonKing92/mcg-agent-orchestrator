using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class GoalRefinementService
{
    // The item can be durable even if the precedent write failed on the original attempt.
    public async Task<bool> TryRecoverResolvedClarificationPrecedentAsync(
        string correlationKey, string answer, int briefVersion,
        CancellationToken cancellationToken = default)
    {
        var goalId = ExtractGoalId(correlationKey);
        var item = (await _collaboration.ListAsync(goalId, cancellationToken))
            .FirstOrDefault(candidate =>
                string.Equals(candidate.CorrelationKey, correlationKey, StringComparison.Ordinal));
        if (item is null || !CollaborationItemLifecycle.IsTerminal(item.Status) ||
            !string.Equals(item.AuthoritativeAnswer?.Text, answer, StringComparison.Ordinal) ||
            item.AuthoritativeAnswer.BriefVersion != briefVersion)
            return false;

        await RecordResolvedClarificationPrecedentAsync(correlationKey, goalId, cancellationToken);
        return true;
    }

    private async Task RecordResolvedClarificationPrecedentAsync(
        string correlationKey, string goalId, CancellationToken cancellationToken)
    {
        var topicKey = ExtractTopicKey(correlationKey);
        if (string.IsNullOrWhiteSpace(topicKey)) return;
        var item = (await _collaboration.ListAsync(goalId, cancellationToken))
            .FirstOrDefault(candidate => string.Equals(candidate.CorrelationKey, correlationKey, StringComparison.Ordinal));
        var authoritativeAnswer = item?.AuthoritativeAnswer;
        if (item is null || authoritativeAnswer is null || string.IsNullOrWhiteSpace(item.GoalId))
            return;
        await _precedents.RecordPrecedentAsync(
            topicKey, authoritativeAnswer.Text, "Operator clarification answer.",
            cancellationToken, item.Id, item.GoalId,
            authoritativeAnswer.Id, authoritativeAnswer.BriefVersion);
    }
}
