using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique filesystem roots and immutable launch descriptions, no global mutations.
public sealed class AcceptanceJobCustodianProjectRegistryTests
{
    [Fact]
    public void ProjectWorkspaceLaunchCarriesItsDataRootAndRegistry()
    {
        using var fixture = new Fixture();
        var info = AcceptanceJobCustodianHost.Bind(fixture.Workspace).CreateStartInfo();
        Assert.Equal(Path.GetFullPath(fixture.Data), info.Environment[OrchestratorDataRoot.EnvironmentVariable]);
        Assert.Equal(Path.Combine(fixture.Data, "projects"),
            info.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable]);
        Assert.Equal(typeof(OrchestratorWorkspace).Assembly.Location, info.ArgumentList[0]);
        Assert.Equal(new[]
        {
            "--repository-root", fixture.Repo,
            "--expected-state-directory", Path.Combine(fixture.Data, "projects", "custody-probe"),
            "--project", "custody-probe", "--tenant", "default", "__acceptance-custodian"
        }, info.ArgumentList.Skip(1));
        Assert.Equal(fixture.Repo, info.WorkingDirectory);
    }

    [Fact]
    public void TenantProjectWorkspaceCarriesTheProjectDataRoot()
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForProject("custody-probe", fixture.Repo, fixture.Repo,
            tenantName: "t1", dataRootDirectory: fixture.Data);
        var info = AcceptanceJobCustodianHost.Bind(workspace).CreateStartInfo();
        Assert.Equal(Path.GetFullPath(fixture.Data), info.Environment[OrchestratorDataRoot.EnvironmentVariable]);
        Assert.Equal(Path.Combine(fixture.Data, "projects"),
            info.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable]);
    }

    [Fact]
    public void ForeignStateDirectoryFailsClosed()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace with { OrchestratorDirectory = Path.Combine(fixture.Root, "wrong-state") };
        Assert.Throws<InvalidOperationException>(() => AcceptanceJobCustodianHost.Bind(workspace));
    }

    [Fact]
    public async Task ProjectCustodianUnderRedirectedProfileReachesCustodyProtocol()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var info = AcceptanceJobCustodianHost.Bind(fixture.Workspace).CreateStartInfo();
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Root, "profile")).FullName;
        info.Environment["USERPROFILE"] = profile;
        info.Environment["HOME"] = profile;
        using var custodian = ProcessTreeGuiSuppression.Start(info)!;
        var output = custodian.StandardOutput.ReadToEndAsync();
        var error = custodian.StandardError.ReadToEndAsync();
        var identity = TestOwnedProcessStop.Identify(custodian);
        try
        {
            custodian.StandardInput.Close();
            try
            {
                await custodian.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException("Project custodian did not exit after its custody input was closed.", exception);
            }
            await output;
            var stderr = await error;
            Assert.Equal(1, custodian.ExitCode);
            Assert.Contains("Acceptance custody failed: Invalid custody request.", stderr);
            Assert.DoesNotContain("Unknown project", stderr);
            Assert.DoesNotContain("differs from expected", stderr);
        }
        finally
        {
            if (!custodian.HasExited)
            {
                Assert.True(TestOwnedProcessStop.StopTreeIfSame(identity), "Could not stop the owned project custodian.");
                await custodian.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public void DefaultWorkspaceLaunchKeepsInheritedEnvironment()
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repo, fixture.Repo, null,
            new OrchestratorProjectRegistry(Path.Combine(fixture.Root, "registry"), fixture.Data));
        var info = AcceptanceJobCustodianHost.Bind(workspace).CreateStartInfo();
        foreach (var name in new[]
        {
            OrchestratorDataRoot.EnvironmentVariable,
            OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable
        })
        {
            info.Environment.TryGetValue(name, out var value);
            Assert.Equal(Environment.GetEnvironmentVariable(name), value);
        }
        Assert.Equal(typeof(OrchestratorWorkspace).Assembly.Location, info.ArgumentList[0]);
        Assert.Equal(new[]
        {
            "--repository-root", fixture.Repo,
            "--expected-state-directory", Path.Combine(fixture.Repo, ".orchestrator"),
            "--project", "default", "--tenant", "default", "__acceptance-custodian"
        }, info.ArgumentList.Skip(1));
        Assert.Equal(fixture.Repo, info.WorkingDirectory);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("custody-project-").FullName;
        internal string Repo { get; }
        internal string Data { get; }
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForProject("custody-probe", Repo, Repo,
            dataRootDirectory: Data);

        internal Fixture()
        {
            Repo = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
            Data = Path.Combine(Root, "data");
            new OrchestratorProjectRegistry(Path.Combine(Data, "projects"), Data).CreateProject("custody-probe", Repo);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
