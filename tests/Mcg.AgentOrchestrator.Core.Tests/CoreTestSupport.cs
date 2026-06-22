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

internal static partial class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition)
    {
        if (condition)
        {
            throw new InvalidOperationException("Expected condition to be false.");
        }
    }

    public static void Single<T>(IReadOnlyCollection<T> values)
    {
        if (values.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one value, got {values.Count}.");
        }
    }

    public static void Empty<T>(IReadOnlyCollection<T> values)
    {
        if (values.Count != 0)
        {
            throw new InvalidOperationException($"Expected no values, got {values.Count}.");
        }
    }

    public static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate))
        {
            throw new InvalidOperationException("Expected matching value was not found.");
        }
    }

    public static void Contains(string value, Func<string, bool> predicate)
    {
        if (!predicate(value))
        {
            throw new InvalidOperationException("Expected matching text was not found.");
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    public static T Single<T>(IReadOnlyList<T> values)
    {
        if (values.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one value, got {values.Count}.");
        }

        return values[0];
    }

    // Faithful delegates to Xunit.Assert for standard overloads this shadow previously lacked (af94964c):
    // value-in-collection membership, substring/collection equality, per-element inspection, prefix/suffix,
    // and null checks. These forward to xUnit so standard assertions compile and behave identically,
    // removing the overload-resolution friction without rewriting the existing bespoke call sites.
    public static void Contains<T>(T expected, IEnumerable<T> collection)
        => Xunit.Assert.Contains(expected, collection);

    public static void Contains<T>(T expected, IEnumerable<T> collection, IEqualityComparer<T> comparer)
        => Xunit.Assert.Contains(expected, collection, comparer);

    public static void Contains(string expectedSubstring, string actualString)
        => Xunit.Assert.Contains(expectedSubstring, actualString);

    public static void DoesNotContain<T>(T expected, IEnumerable<T> collection)
        => Xunit.Assert.DoesNotContain(expected, collection);

    public static void DoesNotContain(string expectedSubstring, string actualString)
        => Xunit.Assert.DoesNotContain(expectedSubstring, actualString);

    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        => Xunit.Assert.Equal(expected, actual);

    public static void Collection<T>(IEnumerable<T> collection, params Action<T>[] elementInspectors)
        => Xunit.Assert.Collection(collection, elementInspectors);

    public static void StartsWith(string expectedStartString, string? actualString)
        => Xunit.Assert.StartsWith(expectedStartString, actualString);

    public static void EndsWith(string expectedEndString, string? actualString)
        => Xunit.Assert.EndsWith(expectedEndString, actualString);

    public static void NotNull(object? @object)
        => Xunit.Assert.NotNull(@object);

    public static void Null(object? @object)
        => Xunit.Assert.Null(@object);
}
