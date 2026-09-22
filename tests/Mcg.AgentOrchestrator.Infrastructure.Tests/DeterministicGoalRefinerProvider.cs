using Mcg.AgentOrchestrator.Core;

internal sealed class DeterministicGoalRefinerProvider : IModelProvider
{
    public string ProviderName => "deterministic-refiner";

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        const string response = """
            ```json
            {
              "behavioralContract": "Create a deterministic goal.",
              "acceptanceCriteria": ["The goal output remains compatible."],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
    }
}
