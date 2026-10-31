using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: registry, project artifacts, dialogs and answer submission are fixture-owned.
public sealed class OnboardingQuestionResolutionTests
{
    [Fact]
    public async Task IntegrationBranch_ShowsCommandUntilRegistryRecordsResolution()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("conduct-tail");
        OnboardingConsoleFixture.Store(project,
            OnboardingConsoleFixture.Model(LearnedIntegrationBranch.OwnerQuestionKey) with
            {
                IntegrationBranch = new("main", OnboardingConsoleFixture.Source, FactConfidence.Low)
            });
        var harness = new OwnerConsoleHarness(fixture.Clock);
        var board = OwnerConsoleBoardSource.FromRegistry(project, fixture.Home, fixture.Registry, fixture.Clock);
        var source = new OnboardingQuestionSource(harness.Questions, board.InScopeProjects, fixture.Clock);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, source,
            harness.Liveness, new Epics(), fixture.Clock);
        Assert.True(OnboardingConsoleFixture.Read(project).IsOnboarding);
        var decision = Assert.Single((await builder.ReadDecisionsAsync(token)).Decisions);
        Assert.False(decision.CanAnswer);
        Assert.Null(decision.ProposedDefault);
        Assert.Contains("Resolve with: project use-learned-branch conduct-tail", decision.FullText);
        var dialogs = new Dialogs();
        await new OwnerOnboardingDecisionFlow(source, dialogs).RunAsync(decision, false, null, token, null);
        Assert.Equal(decision.FullText, Assert.Single(dialogs.Texts));

        using var output = new StringWriter();
        Assert.Equal(0, ProjectUseLearnedBranchCommand.Execute(
            ["project", "use-learned-branch", "conduct-tail"], fixture.Registry, output));
        Assert.Contains("Use learned integration branch: on", output.ToString());
        Assert.True(fixture.Registry.GetRequiredProject("conduct-tail").UseLearnedIntegrationBranch);
        Assert.Empty((await source.ReadAsync(token)).Live);
        Assert.Empty((await builder.ReadDecisionsAsync(token)).Decisions);
        var state = ProjectOnboardingStateReader.Read(project.ProjectName, project.OrchestratorDirectory,
            project.ConductEventsLogPath, useLearnedBranch: true);
        Assert.True(state.IsOnboarding);
        Assert.Empty(state.OpenQuestions);
        Assert.Single(state.Model!.OwnerQuestions);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected answer box");
    }
}
