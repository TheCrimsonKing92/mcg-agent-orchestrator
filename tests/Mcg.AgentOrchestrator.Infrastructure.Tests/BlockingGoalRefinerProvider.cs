using Mcg.AgentOrchestrator.Core;

internal sealed class BlockingGoalRefinerProvider : IModelProvider
{
    public string ProviderName => "blocking-refiner";

    public ManualResetEventSlim Entered { get; } = new(initialState: false);

    public ManualResetEventSlim Release { get; } = new(initialState: false);

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        Entered.Set();
        Release.Wait(cancellationToken);
        const string response = """
            ```json
            {
              "behavioralContract": "Create the goal after unlocked refinement.",
              "acceptanceCriteria": ["The goal is committed atomically."],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
    }
}
