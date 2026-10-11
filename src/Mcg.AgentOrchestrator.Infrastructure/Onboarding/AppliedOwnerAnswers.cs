using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Read-time owner facts and the answers that cannot be applied to this snapshot.</summary>
public sealed record AppliedOwnerAnswers(
    IReadOnlyList<ProjectOwnerQuestion> OpenQuestions,
    IReadOnlyList<AppliedOwnerAnswers.AnsweredFact> AnsweredFacts,
    IReadOnlyList<AppliedOwnerAnswers.StaleAnswer> StaleAnswers)
{
    public sealed record AnsweredFact(string FactKey, ProjectFact<string> Fact, string Reason);
    public sealed record StaleAnswer(OwnerAnswer Answer, string Reason);
}
