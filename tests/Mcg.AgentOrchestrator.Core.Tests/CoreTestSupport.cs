global using static CoreTestData;

using Mcg.AgentOrchestrator.Core;

internal static class CoreTestData
{
    public static IReadOnlyList<AgentDefinition> DefaultAgents()
    {
        static ModelProfile OpenAi(string reasoningEffort) =>
            new("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey, reasoningEffort);

        return
        [
            new(AgentId.New(), "Planner", AgentRole.Planner, OpenAi("high")),
            new(AgentId.New(), "Researcher", AgentRole.Researcher, OpenAi("high")),
            new(AgentId.New(), "Developer", AgentRole.Developer, OpenAi("medium")),
            new(AgentId.New(), "Tester", AgentRole.Tester, OpenAi("high")),
            new(AgentId.New(), "Reviewer", AgentRole.Reviewer, OpenAi("high"))
        ];
    }
}
internal sealed class FakeClock : IClock
{
    private DateTimeOffset _utcNow = new(2026, 06, 01, 12, 00, 00, TimeSpan.Zero);

    public DateTimeOffset UtcNow => _utcNow;

    public void Advance() => _utcNow = _utcNow.AddSeconds(1);

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}

internal sealed class FakeModelProvider : IModelProvider
{
    private readonly string _responseText;
    private readonly Exception? _exception;
    private readonly ModelUsage _usage;
    private readonly string _stopReason;

    public FakeModelProvider(
        string providerName,
        string responseText,
        Exception? exception = null,
        ModelUsage? usage = null,
        string stopReason = "stop")
    {
        ProviderName = providerName;
        _responseText = responseText;
        _exception = exception;
        _usage = usage ?? new ModelUsage(100, 25);
        _stopReason = stopReason;
    }

    public string ProviderName { get; }

    public int CallCount { get; private set; }

    public ModelRequest? LastRequest { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;

        if (_exception is not null)
        {
            throw _exception;
        }

        return Task.FromResult(new ModelResponse(_responseText, _usage, _stopReason));
    }
}
