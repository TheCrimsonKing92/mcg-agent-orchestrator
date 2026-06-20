namespace Mcg.AgentOrchestrator.Core;

public enum VerificationClass
{
    TestVerifiable,
    RealWorldDependent
}

public sealed record RefinedSpecDecision(string Question, string Choice, string Rationale);

public sealed record RefinedSpecOpenQuestion(
    string Id,
    string Question,
    string ForkKind,
    string Status,
    string? Answer = null);

public sealed record RefinedSpec(
    string BehavioralContract,
    IReadOnlyList<string> AcceptanceCriteria,
    VerificationClass VerificationClass,
    IReadOnlyList<RefinedSpecDecision> Decisions,
    IReadOnlyList<RefinedSpecOpenQuestion> OpenQuestions)
{
    public bool HasOpenQuestions => OpenQuestions.Any(q =>
        string.Equals(q.Status, "Open", StringComparison.OrdinalIgnoreCase));
}
