using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: persisted answers and all console collaborators are fixture-owned.
public sealed class OnboardingStaleAnswerDecisionTests
{
    [Fact]
    public async Task StaleAnswer_DuringOnboarding_RemainsDiagnosticWithoutDecision()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("stale");
        const string staleKey = "commands/removed/test";
        var model = OnboardingConsoleFixture.Model();
        OnboardingConsoleFixture.Store(project, model);
        OwnerAnswerFile.Write(project.OrchestratorDirectory,
            [new(staleKey, "npm test", null, fixture.Clock.GetUtcNow().ToUniversalTime())]);
        var before = File.ReadAllBytes(Path.Combine(project.OrchestratorDirectory, OwnerAnswerFile.FileName));
        var state = OnboardingConsoleFixture.Read(project);
        Assert.True(state.IsOnboarding);
        Assert.Equal(staleKey, Assert.Single(state.StaleAnswers).Answer.FactKey);
        Assert.Equal(0, state.OpenQuestionCount);
        var harness = new OwnerConsoleHarness(fixture.Clock);
        var source = new OnboardingQuestionSource(harness.Questions,
            () => [OnboardingConsoleFixture.Describe(project)], fixture.Clock);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, source,
            harness.Liveness, new Epics(), fixture.Clock);
        Assert.Empty((await source.ReadAsync(token)).Live);
        Assert.Empty((await builder.ReadDecisionsAsync(token)).Decisions);
        // project discover uses this renderer; exclusion from Decisions must preserve diagnostics.
        using var output = new StringWriter();
        ProjectAnswerCommand.WriteAnswerLines(output,
            OwnerAnswerApplier.Apply(model, OwnerAnswerFile.Read(project.OrchestratorDirectory)));
        Assert.Contains($"Stale owner answer: {staleKey} = npm test", output.ToString());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(project.OrchestratorDirectory, OwnerAnswerFile.FileName)));
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }
}
