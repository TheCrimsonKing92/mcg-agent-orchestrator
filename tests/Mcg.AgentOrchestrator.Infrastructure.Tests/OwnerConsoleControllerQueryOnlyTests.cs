using System.Reflection;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reflection and per-test harness state and dialogs only.
public sealed class OwnerConsoleControllerQueryOnlyTests
{
    [Fact]
    public void Constructors_AllInstanceConstructors_RequireQueryWithoutStores()
    {
        var constructors = Inspect(typeof(OwnerConsoleScreenController));

        Assert.NotEmpty(constructors);
        foreach (var constructor in constructors)
        {
            Assert.Empty(constructor.StoreParameterTypes);
            Assert.False(Assert.Single(constructor.QueryParameterOptional));
        }
    }

    [Fact]
    public void Inspect_OldConstructorShape_ReportsStoresAndOptionalQuery()
    {
        var constructor = Assert.Single(Inspect(typeof(OldConstructorShape)));

        Assert.Equal(new[] { typeof(IOrchestratorStateQueries), typeof(IGoalEventTail) },
            constructor.StoreParameterTypes);
        Assert.True(Assert.Single(constructor.QueryParameterOptional));
    }

    [Fact]
    public async Task RunGoalCommand_FactoryDefaultQuery_MatchesFormatterTextForPrefix()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("55555555-detail", "Readable detail", AgentRole.Developer);
        harness.AddGoal("66666666-other", "Other goal", AgentRole.Developer);
        var dialogs = new Dialogs();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);

        await controller.RunCommandAsync(":goal 5555",
            cancellationToken: TestContext.Current.CancellationToken);

        var output = new OwnerConsoleHarness.FakeOutput();
        await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, harness.Tail, "55555555-detail",
            output, TestContext.Current.CancellationToken, harness.Clock.LocalTimeZone);
        var expected = string.Join(Environment.NewLine, output.Text.Replace("\r", "").TrimEnd('\n').Split('\n'));
        Assert.Equal(expected, Assert.Single(dialogs.Texts));
    }

    private static ConstructorInspection[] Inspect(Type type) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(constructor =>
            {
                var parameters = constructor.GetParameters();
                return new ConstructorInspection(
                    parameters.Where(parameter =>
                            typeof(IOrchestratorStateQueries).IsAssignableFrom(parameter.ParameterType) ||
                            typeof(IGoalEventTail).IsAssignableFrom(parameter.ParameterType))
                        .Select(parameter => parameter.ParameterType).ToArray(),
                    parameters.Where(parameter => parameter.ParameterType == typeof(OwnerGoalDetailQuery))
                        .Select(parameter => parameter.IsOptional || parameter.HasDefaultValue).ToArray());
            }).ToArray();

    private sealed record ConstructorInspection(Type[] StoreParameterTypes, bool[] QueryParameterOptional);

    private sealed class OldConstructorShape
    {
        public OldConstructorShape(IOrchestratorStateQueries state, IGoalEventTail tail,
            OwnerGoalDetailQuery? goalDetail = null) { }
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];

        public Task ShowTextAsync(string title, string text)
        {
            Texts.Add(text);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException();
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException();
    }
}
