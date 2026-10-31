using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only fixture-owned persisted project artifacts are read or written.
public sealed class ProjectOnboardingStateReaderTests
{
    [Fact]
    public void RegisteredProjects_ReportEarliestIncompleteStep_OnlyLiveConductorIsComplete()
    {
        using var fixture = new OnboardingConsoleFixture();
        var registered = fixture.Register("registered");
        var discovered = fixture.Register("discovered");
        var installed = fixture.Register("installed");
        var live = fixture.Register("live");
        foreach (var project in new[] { discovered, installed, live })
            OnboardingConsoleFixture.Store(project, OnboardingConsoleFixture.Model(
                OnboardingConsoleFixture.TestKey, OnboardingConsoleFixture.BuildKey));
        foreach (var project in new[] { installed, live })
        {
            fixture.AnswerAll(project, OnboardingConsoleFixture.TestKey, OnboardingConsoleFixture.BuildKey);
            OnboardingConsoleFixture.InstallManifest(project);
        }
        fixture.StartConductor(live);
        Assert.Equal(4, fixture.Registry.ListProjects().Count);
        var states = new[] { registered, discovered, installed, live }.Select(OnboardingConsoleFixture.Read).ToArray();
        Assert.Equal(new ProjectOnboardingStep?[] { ProjectOnboardingStep.Discovered,
            ProjectOnboardingStep.QuestionsAnswered, ProjectOnboardingStep.ConductorStarted, null },
            states.Select(state => state.CurrentStep));
        Assert.Equal(new[] { true, true, true, false }, states.Select(state => state.IsOnboarding));
        Assert.Equal("stack unknown", states[0].Stack);
        Assert.Equal("declared", states[1].Stack);
        Assert.Equal(2, states[1].OpenQuestionCount);
        Assert.Equal("manifest installed · conductor not started", states[2].Progress);
        Assert.All(states, state => Assert.Null(state.Unavailable));
    }

    [Fact]
    public void CurrentAndSuggestedValues_AreOpen_StaleAnswersDoNotCountAsOpen()
    {
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("conduct-tail");
        OnboardingConsoleFixture.Store(project, OnboardingConsoleFixture.Model(OnboardingConsoleFixture.TestKey));
        OwnerAnswerFile.Write(project.OrchestratorDirectory,
            [new("environment/obsolete", "old", null, fixture.Clock.GetUtcNow().ToUniversalTime())]);
        var state = OnboardingConsoleFixture.Read(project);
        Assert.Equal(1, state.OpenQuestionCount);
        Assert.Single(state.StaleAnswers);
        fixture.AnswerAll(project, OnboardingConsoleFixture.TestKey);
        state = OnboardingConsoleFixture.Read(project);
        Assert.Empty(state.OpenQuestions);
        Assert.Equal(ProjectOnboardingStep.ManifestInstalled, state.CurrentStep);
    }

    [Fact]
    public void RuntimeEvidence_CompletesWithoutModel_AndReadsPreserveArtifacts()
    {
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("incomplete");
        Directory.CreateDirectory(project.OrchestratorDirectory);
        // Runtime evidence ends onboarding even when earlier artifacts are absent or malformed.
        File.WriteAllText(Path.Combine(project.OrchestratorDirectory, OwnerAnswerFile.FileName), "broken");
        OnboardingConsoleFixture.InstallManifest(project);
        fixture.StartConductor(project);
        var before = OnboardingConsoleFixture.Snapshot(project);
        var state = OnboardingConsoleFixture.Read(project);
        Assert.False(state.IsOnboarding);
        Assert.Empty(state.OpenQuestions);
        Assert.Null(state.Unavailable);
        Assert.Equal(before.OrderBy(pair => pair.Key), OnboardingConsoleFixture.Snapshot(project).OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("project-model.json", (int)ProjectOnboardingStep.Discovered)]
    [InlineData("owner-answers.json", (int)ProjectOnboardingStep.QuestionsAnswered)]
    [InlineData("acceptance-manifest.json", (int)ProjectOnboardingStep.ManifestInstalled)]
    public void MalformedArtifacts_ReportProjectSpecificUnavailableWithoutMutation(string file, int step)
    {
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("damaged");
        OnboardingConsoleFixture.Store(project, OnboardingConsoleFixture.Model());
        OnboardingConsoleFixture.InstallManifest(project);
        File.WriteAllText(Path.Combine(project.OrchestratorDirectory, file), "{broken");
        var before = OnboardingConsoleFixture.Snapshot(project);
        var state = OnboardingConsoleFixture.Read(project);
        Assert.True(state.IsOnboarding);
        Assert.Equal((ProjectOnboardingStep)step, state.CurrentStep);
        Assert.Contains("damaged:", state.Unavailable);
        Assert.StartsWith("unavailable:", state.Progress);
        Assert.Equal(before.OrderBy(pair => pair.Key), OnboardingConsoleFixture.Snapshot(project).OrderBy(pair => pair.Key));
        var healthy = fixture.Register("healthy");
        OnboardingConsoleFixture.Store(healthy, OnboardingConsoleFixture.Model());
        Assert.Null(OnboardingConsoleFixture.Read(healthy).Unavailable);
    }

    [Fact]
    public void HistoricalConductorEvent_CompletesFinalStep_ButUnrelatedEventsDoNot()
    {
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("history");
        OnboardingConsoleFixture.Store(project, OnboardingConsoleFixture.Model());
        OnboardingConsoleFixture.InstallManifest(project);
        var log = new ConductEventLogWriter(project.ConductEventsLogPath);
        log.Append("operator-intent", null, "answer");
        Assert.Equal(ProjectOnboardingStep.ConductorStarted, OnboardingConsoleFixture.Read(project).CurrentStep);
        log.Append("loop-start", null, "started");
        Assert.False(OnboardingConsoleFixture.Read(project).IsOnboarding);
    }
}
