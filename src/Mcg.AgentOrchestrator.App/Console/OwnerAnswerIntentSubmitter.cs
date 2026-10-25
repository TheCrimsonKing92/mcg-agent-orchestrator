using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerAnswerIntentSubmitter(OrchestratorWorkspace workspace)
{
    internal OwnerAnswerSubmission Submit(OwnerQuestion question, string answer, string idempotencyKey)
    {
        var kind = question.Kind switch
        {
            OwnerQuestionKind.Clarification => OperatorAnswerTargetKind.Clarification,
            OwnerQuestionKind.HumanInput => OperatorAnswerTargetKind.HumanInput,
            _ => throw new ArgumentException("Question kind does not accept an attention answer.", nameof(question))
        };
        if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("Answer is required.", nameof(answer));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));

        var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), idempotencyKey,
                OperatorIntentVerbs.Answer, question.GoalId, null,
                JsonSerializer.Serialize(new AnswerOperatorIntentPayload(kind, question.ItemId, question.GoalId,
                    answer, OperatorActorKind.Human, [], null), OperatorIntentJson.Options),
                [], "operator", "owner-console", "local-process", DateTimeOffset.UtcNow,
                ActorKind: OperatorActorKind.Human)).GetAwaiter().GetResult();
        return new(persisted.Id);
    }
}
