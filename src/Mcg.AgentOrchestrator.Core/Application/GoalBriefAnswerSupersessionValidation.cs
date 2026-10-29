namespace Mcg.AgentOrchestrator.Core;

internal static class GoalBriefAnswerSupersessionValidation
{
    internal static void Validate(
        GoalId goalId,
        IReadOnlyList<GoalBriefAnswerSupersession> answerSupersessions,
        IReadOnlyDictionary<HumanInputRequestId, HumanInputRequest> requests)
    {
        var duplicateSupersession = answerSupersessions
            .GroupBy(supersession => supersession.RequestId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSupersession is not null)
        {
            throw new ArgumentException(
                $"Clarification '{duplicateSupersession.Key}' can be superseded only once per brief revision.",
                nameof(answerSupersessions));
        }

        foreach (var supersession in answerSupersessions)
        {
            if (!requests.TryGetValue(supersession.RequestId, out var request) || request.GoalId != goalId)
            {
                throw new KeyNotFoundException(
                    $"Human input request '{supersession.RequestId}' was not found on goal '{goalId.Value}'.");
            }

            if (!request.IsCompleted || request.WasDismissed || request.AuthoritativeAnswer is null)
            {
                throw new InvalidOperationException(
                    $"Human input request '{supersession.RequestId}' has no answered clarification to supersede.");
            }

            if (string.IsNullOrWhiteSpace(supersession.ReplacementAnswer))
            {
                throw new ArgumentException("Replacement clarification answer cannot be empty.", nameof(answerSupersessions));
            }
        }
    }
}
