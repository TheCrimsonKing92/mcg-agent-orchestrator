using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns source/data fixtures, its forge fake and AsyncLocal console capture.
public sealed class ProjectDiscoverDeliveryQuestionTests
{
    [Theory]
    [InlineData(PullRequestRequirementKind.Required, "main")]
    [InlineData(PullRequestRequirementKind.Required, "release/next")]
    [InlineData(PullRequestRequirementKind.NotRequired, "main")]
    [InlineData(PullRequestRequirementKind.Unknown, "main")]
    public void Discovery_RecordsProbeAndAsksExactlyOnceOnlyWhenRequired(PullRequestRequirementKind kind, string branch)
    {
        using var fixture = new TestProject(branch);
        var probe = new FakeProbe(new(kind, "fixture probe reason"));
        var registryBefore = File.ReadAllBytes(fixture.Registry.RegistryPath);
        var questionText = $"The {branch} branch requires pull requests. Deliver by opening a pull request instead of pushing to {branch}?";

        for (var round = 0; round < 2; round++)
        {
            var (model, output) = fixture.Discover(probe);
            Assert.Equal(new DeliveryProbeRecord(kind.ToString(), "fixture probe reason"), model.DeliveryProbe);
            var questions = model.OwnerQuestions.Where(question => question.FactKey == "delivery").ToArray();
            var questionLines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(questionText, StringComparison.Ordinal)).ToArray();
            if (kind == PullRequestRequirementKind.Required)
            {
                var question = Assert.Single(questions);
                Assert.Equal(questionText, question.Question);
                Assert.Equal("pull-request", question.SuggestedValue);
                Assert.Equal($"Owner question: {questionText} (suggested: pull-request)", Assert.Single(questionLines));
            }
            else
            {
                Assert.Empty(questions);
                Assert.Empty(questionLines);
                Assert.DoesNotContain("(suggested: pull-request)", output);
            }
            var reopened = fixture.Reopen();
            Assert.Equal("direct", reopened.GetRequiredProject("sample").Delivery);
            Assert.Null(reopened.GetRequiredProject("sample").DeliveryAnswer);
            Assert.Contains($"Delivery probe: {kind}: fixture probe reason", fixture.Show(reopened));
            Assert.Equal(registryBefore, File.ReadAllBytes(fixture.Registry.RegistryPath));
        }

        Assert.Equal(2, probe.Calls.Count);
        Assert.All(probe.Calls, call =>
        {
            Assert.Equal(fixture.Source.Root, call.Root);
            Assert.Equal(branch, call.Branch);
        });
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pull-request")]
    public void RequiredProbe_AfterDeliveryAnswer_DoesNotAskAgainOrOverrideOwner(string mode)
    {
        using var fixture = new TestProject("main");
        var probe = new FakeProbe(PullRequestRequirement.Required("required by ruleset"));
        Assert.Single(fixture.Discover(probe).Model.OwnerQuestions.Where(question => question.FactKey == "delivery"));
        using var answerOutput = new StringWriter();
        Assert.Equal(0, ProjectCliCommand.Execute(["project", "answer", "sample", "delivery", mode],
            fixture.Registry, fixture.Home.Root, null, answerOutput));
        Assert.NotNull(fixture.Reopen().GetRequiredProject("sample").DeliveryAnswer);

        var (model, output) = fixture.Discover(probe);

        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == "delivery");
        Assert.DoesNotContain("(suggested: pull-request)", output);
        Assert.Equal(mode, fixture.Reopen().GetRequiredProject("sample").Delivery);
        Assert.Equal(new DeliveryProbeRecord("Required", "required by ruleset"), model.DeliveryProbe);
        Assert.Equal(2, probe.Calls.Count);
    }

    [Fact]
    public void DeciderAnswerAlone_DoesNotSuppressDeliveryQuestion()
    {
        using var fixture = new TestProject("main");
        using var output = new StringWriter();
        Assert.Equal(0, ProjectCliCommand.Execute(["project", "answer", "sample", "delivery-decider", "octo"],
            fixture.Registry, fixture.Home.Root, null, output));
        var probe = new FakeProbe(PullRequestRequirement.Required("required by ruleset"));

        var (model, _) = fixture.Discover(probe);

        Assert.Equal("pull-request", Assert.Single(model.OwnerQuestions.Where(question => question.FactKey == "delivery")).SuggestedValue);
        var project = fixture.Reopen().GetRequiredProject("sample");
        Assert.Equal("octo", project.DeliveryDecider);
        Assert.Equal("direct", project.Delivery);
        Assert.Null(project.DeliveryAnswer);
        Assert.Single(probe.Calls);
    }

    [Fact]
    public void ThrowingProbe_DoesNotFailDiscoveryAndRecordsUnknownReason()
    {
        using var fixture = new TestProject("main");
        var probe = new FakeProbe(PullRequestRequirement.Required("unused")) { Throw = true };

        var (model, output) = fixture.Discover(probe);

        Assert.Equal(new DeliveryProbeRecord("Unknown", "forge probe failed: fixture forge failed"), model.DeliveryProbe);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey == "delivery");
        Assert.DoesNotContain("(suggested: pull-request)", output);
        Assert.Equal("direct", fixture.Reopen().GetRequiredProject("sample").Delivery);
        Assert.Contains("Delivery probe: Unknown: forge probe failed: fixture forge failed", fixture.Show(fixture.Reopen()));
        Assert.Single(probe.Calls);
    }

    private sealed class FakeProbe(PullRequestRequirement result) : IPullRequestRequirementProbe
    {
        public List<(string Root, string Branch)> Calls { get; } = [];
        public bool Throw { get; init; }

        public PullRequestRequirement Probe(string repositoryRoot, string branch)
        {
            Calls.Add((repositoryRoot, branch));
            if (Throw) throw new InvalidOperationException("fixture forge failed");
            return result;
        }
    }

    private sealed class TestProject : IDisposable
    {
        public ProjectOnboardingFixture Home { get; } = new("no-solution");
        public ProjectOnboardingFixture Source { get; } = new("solution");
        public OrchestratorProjectRegistry Registry { get; }
        private string ModelPath { get; }

        public TestProject(string branch)
        {
            ProjectOnboardingFixture.SeedRemoteDefault(Source.Root);
            Registry = new(Path.Combine(Home.Root, "registry"), Path.Combine(Home.Root, "data"));
            ModelPath = Path.Combine(Registry.CreateProject("sample", Source.Root, branch).ResolveWorkspace().OrchestratorDirectory,
                "project-model.json");
        }

        public OrchestratorProjectRegistry Reopen() => new(Registry.RegistryDirectory, Registry.DataRootDirectory);

        public (ProjectModel Model, string Output) Discover(IPullRequestRequirementProbe probe)
        {
            using var output = new StringWriter();
            Assert.Equal(0, ProjectCliCommand.Execute(["project", "discover", "sample"], Registry, Home.Root, null, output,
                measurerFactory: () => throw new InvalidOperationException("Unexpected measurement."),
                pullRequestProbeFactory: () => probe));
            Assert.True(File.Exists(ModelPath), "Discovery must write its project model.");
            return (ProjectModelJson.Deserialize(File.ReadAllText(ModelPath)), output.ToString());
        }

        public string Show(OrchestratorProjectRegistry registry) => CaptureConsole(() => Assert.Equal(0,
            ProjectCliCommand.Execute(["project", "show", "sample"], registry, Home.Root, null)));

        public void Dispose()
        {
            Source.Dispose();
            Home.Dispose();
        }
    }
}
