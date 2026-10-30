namespace Mcg.AgentOrchestrator.Core;

public sealed record ProjectOwnerQuestion(string FactKey, string Question, FactSource Source)
{
    public string? SuggestedValue { get; init; }
}
