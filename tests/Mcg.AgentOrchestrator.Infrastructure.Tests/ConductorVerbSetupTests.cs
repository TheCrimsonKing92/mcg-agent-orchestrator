using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: private workspace directories, injected process/lock seams, no real launcher.
public sealed class ConductorVerbSetupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(4321)]
    public void Setup_WithOrWithoutOwner_PrintsRecordedVersionAndIsRepeatable(int? owner)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        var launcher = new ConductorVerbStartTests.RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        for (var run = 0; run < 2; run++)
        {
            output.GetStringBuilder().Clear();
            Assert.Equal(0, CliConductorCommand.Run(["conductor", "setup"], workspace,
                launcher, new ConductorVerbStartTests.FixedProbe(owner), output: output, error: error));
            Assert.Equal($"portfolio: version 1 ({workspace.PortfolioStorePath}){Environment.NewLine}", output.ToString());
            Assert.NotNull(PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath));
        }
        Assert.Empty(launcher.Requests);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Setup_LivenessProbeFails_DoesNotConsultProbe()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "setup"], fixture.Workspace,
            lockProbe: new UnavailableProbe(), output: output, error: error));
        Assert.Contains("portfolio: version 1", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Start_DefaultSetup_PortfolioIsReadableWhenLauncherIsCalled()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var launcher = new InspectingLauncher(fixture.Workspace);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new ConductorVerbStartTests.FixedProbe(null), output: output, error: error,
            home: OrchestratorHome.Resolve(fixture.Workspace.RootDirectory, _ => null)));
        Assert.Equal(1, launcher.Calls);
        Assert.Equal("", error.ToString());
        Assert.DoesNotContain("portfolio:", output.ToString());
    }

    [Theory]
    [InlineData("setup", false)]
    [InlineData("start", false)]
    [InlineData("setup", true)]
    [InlineData("start", true)]
    public void SetupFailure_BothVerbs_ReturnErrorWithoutLaunching(string verb, bool sqlite)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var launcher = new ConductorVerbStartTests.RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;
        Assert.Equal(1, CliConductorCommand.Run(["conductor", verb], fixture.Workspace,
            launcher, new ConductorVerbStartTests.FixedProbe(null), output: output, error: error,
            home: OrchestratorHome.Resolve(fixture.Workspace.RootDirectory, _ => null), storeSetup: directory =>
            {
                calls++;
                Assert.Equal(fixture.Workspace.OrchestratorDirectory, directory);
                if (sqlite)
                    throw new SqliteException("setup failed", 11);
                throw new InvalidOperationException("setup failed");
            }));
        Assert.Equal(1, calls);
        Assert.Equal($"Error: setup failed{Environment.NewLine}", error.ToString());
        Assert.Equal("", output.ToString());
        Assert.Empty(launcher.Requests);
    }

    private sealed class UnavailableProbe : IConductorLockProbe
    {
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace) =>
            throw new InvalidOperationException("probe unavailable");
    }

    private sealed class InspectingLauncher(OrchestratorWorkspace workspace) : IConductorProcessLauncher
    {
        public int Calls { get; private set; }
        public ConductorLaunchResult Launch(ConductorLaunchRequest request)
        {
            Calls++;
            Assert.True(File.Exists(workspace.PortfolioStorePath));
            Assert.NotNull(PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath));
            return new(0, "{\"pid\":1234,\"stdoutPath\":\"stdout.log\"}", null);
        }
    }
}
