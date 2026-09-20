using Mcg.AgentOrchestrator.Core;

internal sealed class BarrierGoalRefinerProvider(int expectedCalls) : IModelProvider
{
    private readonly CountdownEvent _entered = new(expectedCalls);

    public string ProviderName => "barrier-refiner";

    public WaitHandle AllEntered => _entered.WaitHandle;

    public ManualResetEventSlim Release { get; } = new(initialState: false);

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        _entered.Signal();
        Release.Wait(cancellationToken);
        const string response = """
            ```json
            {
              "behavioralContract": "Create exactly one linked goal.",
              "acceptanceCriteria": ["Exactly one goal is linked to the source backlog item."],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
    }
}
