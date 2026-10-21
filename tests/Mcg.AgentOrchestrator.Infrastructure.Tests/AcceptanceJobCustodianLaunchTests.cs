using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: unique filesystem roots and immutable launch descriptions, no global mutations.
public sealed class AcceptanceJobCustodianLaunchTests
{
    [Fact]
    public async Task ProductionClaimLaunchesCompiledCustodyAndSettles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        const string probe = "Mcg.AgentOrchestrator.SupervisorLaunchProbe";
        var closure = FixtureProbeLocator.FindDirectory(typeof(AcceptanceJobCustodianLaunchTests), probe,
            [probe + ".dll", probe + ".runtimeconfig.json"]);
        Assert.NotNull(closure);
        var start = AcceptanceJobCustodianHost.Bind(fixture.Workspace).CreateStartInfo();
        start.ArgumentList.Clear();
        foreach (var arg in new[] { Path.Combine(closure, probe + ".dll"), "acceptance-claim", fixture.Root, Guid.NewGuid().ToString("N") })
            start.ArgumentList.Add(arg);
        using var holder = ProcessTreeGuiSuppression.Start(start)!;
        var error = holder.StandardError.ReadToEndAsync();
        var identity = SpawnProcessIdentityReader.ReadForRegistration(holder).Identity!;
        Process? custody = null;
        try
        {
            var line = await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(string.IsNullOrWhiteSpace(line), holder.HasExited ? await error : "Claim receipt absent.");
            using var receipt = JsonDocument.Parse(line!);
            var root = receipt.RootElement;
            var custodyIdentity = root.GetProperty("Custodian").Deserialize<SpawnProcessIdentity>()!;
            Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(custodyIdentity.ImagePath));
            custody = Process.GetProcessById(custodyIdentity.ProcessId);
            Assert.False(custody.SafeHandle.IsInvalid);
            Assert.True(SpawnProcessIdentityReader.TryRead(custody, out var live));
            Assert.Equal(SpawnTrackedProcessStatus.LiveMatch,
                SpawnProcessIdentityReader.EvaluateRecordedIdentity(custodyIdentity, live, out _));
            var lease = root.GetProperty("Lease");
            Assert.Equal("Claimed", lease.GetProperty("Phase").GetString());
            Assert.Equal("job-custody-ready", lease.GetProperty("Receipts")[0].GetProperty("Reason").GetString());
            var jobName = lease.GetProperty("JobName").GetString()!;
            var observation = DurableAcceptanceJob.Observe(jobName, identity, SpawnTrackedProcessStatus.LiveMatch, false);
            Assert.True(observation.HolderMatches, observation.Detail);
            Assert.False(observation.Settled);
            var settlement = root.GetProperty("SettlementPath").GetString()!;
            Assert.False(File.Exists(settlement));
            holder.StandardInput.WriteLine("exit");
            holder.StandardInput.Close();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await custody.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, holder.ExitCode);
            Assert.Equal(0, custody.ExitCode);
            Assert.Equal(new AcceptanceJobSettlement(jobName, identity, true),
                JsonSerializer.Deserialize<AcceptanceJobSettlement>(File.ReadAllText(settlement)));
            Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
        }
        finally
        {
            TestOwnedProcessStop.StopRootIfSame(holder, identity, TimeSpan.FromSeconds(30));
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (custody is not null)
            {
                await custody.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                custody.Dispose();
            }
        }
    }

    [Fact]
    public void BoundLaunchUsesCurrentClosureAndExplicitWorkspace()
    {
        using var fixture = new Fixture();
        var info = AcceptanceJobCustodianHost.Bind(fixture.Workspace).CreateStartInfo();
        Assert.True(Path.IsPathFullyQualified(info.FileName));
        Assert.True(File.Exists(info.FileName));
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(info.FileName));
        Assert.Equal(typeof(OrchestratorWorkspace).Assembly.Location, info.ArgumentList[0]);
        Assert.Contains(fixture.Workspace.RootDirectory, info.ArgumentList);
        Assert.Contains(fixture.Workspace.OrchestratorDirectory, info.ArgumentList);
        Assert.Contains(AcceptanceJobCustodianHost.Subcommand, info.ArgumentList);
        Assert.Equal(fixture.Workspace.ExecutionDirectory, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
    }

    [Theory]
    [InlineData("missing-host")]
    [InlineData("relative-host")]
    [InlineData("wrong-host")]
    [InlineData("wrong-app")]
    [InlineData("unknown-runtime")]
    public void UnestablishedClosureFailsClosed(string fault)
    {
        using var fixture = new Fixture();
        var process = fault switch
        {
            "missing-host" => null,
            "relative-host" => "dotnet.exe",
            "wrong-host" => Path.Combine(fixture.Root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            _ => Environment.ProcessPath
        };
        var application = fault == "wrong-app" ? Path.Combine(fixture.Root, "App.dll") :
            typeof(OrchestratorWorkspace).Assembly.Location;
        var runtime = fault == "unknown-runtime" ? fixture.Root : RuntimeEnvironment.GetRuntimeDirectory();
        Assert.Throws<InvalidOperationException>(() =>
            AcceptanceJobCustodianHost.Bind(process, application, runtime, fixture.Workspace));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("custody-launch-").FullName;
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(Root, Root, null,
            new OrchestratorProjectRegistry(Path.Combine(Root, "registry"), Path.Combine(Root, "data")));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
