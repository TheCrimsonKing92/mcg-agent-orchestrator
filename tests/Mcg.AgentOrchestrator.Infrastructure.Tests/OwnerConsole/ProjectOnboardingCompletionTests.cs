using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: project files, conductor history and databases belong to this fixture.
public sealed class ProjectOnboardingCompletionTests
{
    [Fact]
    public async Task RecordedRun_WithManifestAndOpenQuestions_RemovesOnboardingAttention()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("established");
        var control = fixture.Register("new-project");
        foreach (var workspace in new[] { project, control })
            OnboardingConsoleFixture.Store(workspace,
                OnboardingConsoleFixture.Model(OnboardingConsoleFixture.TestKey));
        OnboardingConsoleFixture.InstallManifest(project);
        var harness = new OwnerConsoleHarness(fixture.Clock);
        Directory.CreateDirectory(Path.GetDirectoryName(project.SqliteStatePath)!);
        _ = StateDbMigrations.EnsureUpToDate(project.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(project.SqliteStatePath)
            .SaveAsync(harness.Kernel, token);
        var boardSource = new OwnerConsoleBoardSource("established", OwnerConsoleProjectScope.All,
            () => [OnboardingConsoleFixture.Describe(project), OnboardingConsoleFixture.Describe(control)],
            fixture.Clock);
        var questions = new OnboardingQuestionSource(harness.Questions,
            boardSource.InScopeProjects, fixture.Clock);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, questions,
            harness.Liveness, new Epics(), fixture.Clock);
        var before = OnboardingConsoleFixture.Read(project);
        Assert.True(before.IsOnboarding);
        Assert.Equal(OnboardingConsoleFixture.TestKey, Assert.Single(before.OpenQuestions).FactKey);
        Assert.Equal(2, (await builder.ReadDecisionsAsync(token)).Decisions.Length);

        // Persist a past run; no live conductor lease or landed goal can satisfy completion.
        Assert.False(new ConductorLeaseLiveness(project.OrchestratorDirectory).IsRunning());
        new ConductEventLogWriter(project.ConductEventsLogPath).Append("loop-start", null, "started");
        var state = OnboardingConsoleFixture.Read(project);
        Assert.False(state.IsOnboarding);
        Assert.Null(state.CurrentStep);
        Assert.Empty(state.OpenQuestions);
        Assert.Equal(0, state.OpenQuestionCount);
        Assert.Single(state.Model!.OwnerQuestions);
        var (decisions, hidden) = await builder.ReadDecisionsAsync(token);
        Assert.Equal("new-project", Assert.Single(decisions).GoalPrefix);
        var board = OwnerConsoleBoardSection.Arrange(builder.WithDecisions(
            await boardSource.ComposeAsync(await builder.BuildBoardAsync(token), token), decisions, hidden));
        var header = Assert.Single(board.Board.Where(row => row.Project == "established" && row.IsDiagnostic));
        Assert.Equal("stopped", header.State);
        Assert.Equal(0, header.OwnerDecisions);
        Assert.DoesNotContain("onboarding", header.Title);
        Assert.DoesNotContain("question", OwnerConsoleBoardSection.HeaderLine(header));
        var controlHeader = Assert.Single(board.Board.Where(row => row.Project == "new-project" && row.IsDiagnostic));
        Assert.Equal("onboarding", controlHeader.State);
        Assert.Contains("1 question open", controlHeader.Title);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }
}
