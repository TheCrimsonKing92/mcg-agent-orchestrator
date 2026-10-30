using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: fixtures, clock and writers are local; show uses AsyncLocal console capture.
public sealed class ProjectDeliveryAnswerCliTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 10, 30, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Answers_WithoutDiscovery_PersistBothValuesReasonsAndTimeAndShowAfterReopen()
    {
        using var fixture = new TestProject();
        Assert.False(File.Exists(fixture.ModelPath));
        const string reason = "  team requires review  ";
        using var modeOutput = new StringWriter();
        Assert.Equal(0, ProjectAnswerCommand.Execute(
            ["project", "answer", "sample", "delivery", "pull-request", "--reason", reason],
            fixture.Registry, fixture.Home.Root, null, modeOutput,
            () => Recorded.AddMilliseconds(987).ToOffset(TimeSpan.FromHours(-5))));
        Assert.Equal("Recorded owner answer: delivery = pull-request (High, owner answer recorded 2026-10-30T12:34:56Z)" +
            Environment.NewLine, modeOutput.ToString());

        using var deciderOutput = new StringWriter();
        Assert.Equal(0, ProjectAnswerCommand.Execute(
            ["project", "answer", "sample", "delivery-decider", "octo", "--reason", "owns merge decision"],
            fixture.Registry, fixture.Home.Root, null, deciderOutput, () => Recorded));
        Assert.Equal("Recorded owner answer: delivery-decider = octo (High, owner answer recorded 2026-10-30T12:34:56Z)" +
            Environment.NewLine, deciderOutput.ToString());

        var reopened = fixture.Reopen();
        var project = reopened.GetRequiredProject("sample");
        Assert.Equal("pull-request", project.Delivery);
        Assert.Equal("octo", project.DeliveryDecider);
        Assert.Equal(new OwnerAnswer("delivery", "pull-request", reason, Recorded), project.DeliveryAnswer);
        Assert.Equal(new OwnerAnswer("delivery-decider", "octo", "owns merge decision", Recorded), project.DeliveryDeciderAnswer);
        var show = CaptureConsole(() => Assert.Equal(0,
            ProjectCliCommand.Execute(["project", "show", "sample"], reopened, fixture.Home.Root, null)));
        var lines = show.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Delivery: pull-request (source: owner answer)", lines);
        Assert.Contains("Delivery decider: octo", lines);
        Assert.False(File.Exists(fixture.ModelPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.ModelPath)!, "owner-answers.json")));
    }

    [Theory]
    [InlineData("named", "PULL-REQUEST", "pull-request")]
    [InlineData("selected", "direct", "direct")]
    [InlineData("override", "pull-request", "pull-request")]
    public void CliAnswer_RoutesDeliveryForEachProjectSelection(string selection, string value, string expected)
    {
        using var fixture = new TestProject();
        if (selection == "selected") fixture.Registry.SelectProject("sample");
        string[] options = selection == "named" ? ["sample", "delivery", value] : ["delivery", value];
        using var output = new StringWriter();

        Assert.Equal(0, ProjectCliCommand.Execute(["project", "answer", .. options], fixture.Registry,
            fixture.Home.Root, selection == "override" ? "sample" : null, output));

        Assert.Contains($"Recorded owner answer: delivery = {expected} (High, owner answer recorded ", output.ToString());
        Assert.Equal(expected, fixture.Reopen().GetRequiredProject("sample").Delivery);
    }

    [Theory]
    [InlineData("delivery", "sideways", "direct or pull-request")]
    [InlineData("delivery", " direct", "direct or pull-request")]
    [InlineData("delivery", "pull-request ", "direct or pull-request")]
    [InlineData("delivery-decider", "@octo", "code-host login")]
    [InlineData("delivery-decider", "octo/team", "code-host login")]
    [InlineData("delivery-decider", "-octo", "code-host login")]
    [InlineData("delivery-decider", "octo-", "code-host login")]
    [InlineData("delivery-decider", "octo--team", "code-host login")]
    [InlineData("delivery-decider", "", "code-host login")]
    [InlineData("delivery-decider", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "code-host login")]
    public void InvalidAnswer_RefusesWithAcceptedFormsAndPreservesExistingRegistration(string key, string value, string accepted)
    {
        using var fixture = new TestProject();
        fixture.Registry.SetDeliveryAnswer("sample", new OwnerAnswer("delivery", "pull-request", "existing", Recorded));
        fixture.Registry.SetDeliveryAnswer("sample", new OwnerAnswer("delivery-decider", "octo", null, Recorded));
        var before = File.ReadAllBytes(fixture.Registry.RegistryPath);
        using var output = new StringWriter();

        Assert.Equal(1, ProjectCliCommand.Execute(["project", "answer", "sample", key, value],
            fixture.Registry, fixture.Home.Root, null, output));

        Assert.StartsWith("Refused: ", output.ToString());
        Assert.Contains(accepted, output.ToString());
        Assert.Equal(before, File.ReadAllBytes(fixture.Registry.RegistryPath));
        Assert.Equal("pull-request", fixture.Reopen().GetRequiredProject("sample").Delivery);
        Assert.Equal("octo", fixture.Reopen().GetRequiredProject("sample").DeliveryDecider);
    }

    [Theory]
    [InlineData("octo-team")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Decider_ValidLoginBoundariesPersist(string login)
    {
        using var fixture = new TestProject();
        using var output = new StringWriter();
        Assert.Equal(0, ProjectCliCommand.Execute(["project", "answer", "sample", "delivery-decider", login],
            fixture.Registry, fixture.Home.Root, null, output));
        var project = fixture.Reopen().GetRequiredProject("sample");
        Assert.Equal(login, project.DeliveryDecider);
        Assert.Equal("direct", project.Delivery);
        Assert.Null(project.DeliveryAnswer);
    }

    [Fact]
    public void DefaultProject_RefusesDeliveryWithoutWritingRegistry()
    {
        using var fixture = new TestProject();
        var before = File.ReadAllBytes(fixture.Registry.RegistryPath);
        using var output = new StringWriter();
        Assert.Equal(1, ProjectCliCommand.Execute(["project", "answer", "default", "delivery", "pull-request"],
            fixture.Registry, fixture.Home.Root, null, output));
        Assert.Contains("register the project with project create to record delivery", output.ToString());
        Assert.Equal(before, File.ReadAllBytes(fixture.Registry.RegistryPath));
    }

    private sealed class TestProject : IDisposable
    {
        public ProjectOnboardingFixture Home { get; } = new("no-solution");
        private ProjectOnboardingFixture Source { get; } = new("solution");
        public OrchestratorProjectRegistry Registry { get; }
        public string ModelPath { get; }

        public TestProject()
        {
            Registry = new(Path.Combine(Home.Root, "registry"), Path.Combine(Home.Root, "data"));
            ModelPath = Path.Combine(Registry.CreateProject("sample", Source.Root).ResolveWorkspace().OrchestratorDirectory,
                "project-model.json");
        }

        public OrchestratorProjectRegistry Reopen() => new(Registry.RegistryDirectory, Registry.DataRootDirectory);

        public void Dispose()
        {
            Source.Dispose();
            Home.Dispose();
        }
    }
}
