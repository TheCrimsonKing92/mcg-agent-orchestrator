using Mcg.AgentOrchestrator.Core;

internal sealed class CallbackGoalRefinerProvider(Action callback) : IModelProvider
{
    private int _invoked;

    public string ProviderName => "callback-refiner";

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _invoked, 1) == 0)
            callback();
        const string response = """
            ```json
            {
              "behavioralContract": "Create a corrected replacement goal.",
              "acceptanceCriteria": ["The replacement is committed atomically."],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
    }
}
