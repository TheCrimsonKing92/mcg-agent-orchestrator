using Mcg.AgentOrchestrator.Core;

internal sealed class ClarifyingGoalRefinerProvider : IModelProvider
{
    private int _invocationCount;

    public string ProviderName => "clarifying-refiner";

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _invocationCount);
        const string response = """
            ```json
            {
              "behavioralContract": "Integrate with an external API selected by the operator.",
              "acceptanceCriteria": ["The selected API contract is implemented."],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [
                {
                  "kind": "external-contract",
                  "topicKey": "goal-create-api-contract",
                  "refinerConfidence": "low",
                  "blastRadius": "high",
                  "question": "Which external API contract should the goal target?",
                  "choice": "",
                  "rationale": "Operator decision required."
                }
              ]
            }
            ```
            """;
        return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
    }
}
