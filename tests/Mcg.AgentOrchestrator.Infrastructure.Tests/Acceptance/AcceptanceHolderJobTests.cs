using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests;

// Real Windows containment control. Every readiness/exit boundary uses a named event or stdin.
public sealed class AcceptanceHolderJobTests
{
    [Fact]
    public async Task NamedJob_SuccessorStillSeesTwoShardsAfterHolderExit_AndCancelsOnlyThatJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CustodyFixture();
        var token = Guid.NewGuid().ToString("N");
        var doneName = $"Local\\acceptance-done-{token}";
        var readyNames = new[] { $"Local\\acceptance-ready-0-{token}", $"Local\\acceptance-ready-1-{token}" };
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset, doneName);
        using var ready0 = new EventWaitHandle(false, EventResetMode.ManualReset, readyNames[0]);
        using var ready1 = new EventWaitHandle(false, EventResetMode.ManualReset, readyNames[1]);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var childCommands = readyNames.Select(ready => Convert.ToBase64String(Encoding.Unicode.GetBytes(
            $"$r=[Threading.EventWaitHandle]::OpenExisting('{ready}');" +
            $"$d=[Threading.EventWaitHandle]::OpenExisting('{doneName}');[void]$r.Set();[void]$d.WaitOne();"))).ToArray();
        var script = "[Console]::WriteLine('ready');[void][Console]::ReadLine();" +
            $"$s=New-Object Diagnostics.ProcessStartInfo;$s.FileName='{executable.Replace("'", "''")}';" +
            "$s.UseShellExecute=$false;$s.CreateNoWindow=$true;" +
            $"$s.Arguments='-NoLogo -NoProfile -NonInteractive -EncodedCommand {childCommands[0]}';" +
            "$p0=[Diagnostics.Process]::Start($s);" +
            $"$s.Arguments='-NoLogo -NoProfile -NonInteractive -EncodedCommand {childCommands[1]}';" +
            "$p1=[Diagnostics.Process]::Start($s);" +
            "[Console]::WriteLine(('shards:{0},{1}' -f $p0.Id,$p1.Id));[void][Console]::ReadLine();";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) startInfo.ArgumentList.Add(arg);
        using var holder = ProcessTreeGuiSuppression.Start(startInfo)!;
        var jobName = $"Local\\acceptance-test-job-{token}";
        var identity = SpawnProcessIdentityReader.ReadForRegistration(holder).Identity;
        try
        {
            using var job = DurableAcceptanceJob.Attach(holder, jobName, fixture.Launch);
            fixture.TrackCustodian(job.CustodianIdentity);
            Assert.NotNull(identity);
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            holder.StandardInput.WriteLine("start-shards");
            holder.StandardInput.Flush();
            Assert.True(ready0.WaitOne(TimeSpan.FromSeconds(30)), "First shard did not reach its event gate.");
            Assert.True(ready1.WaitOne(TimeSpan.FromSeconds(30)), "Second shard did not reach its event gate.");
            var line = await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("shards:", line, StringComparison.Ordinal);
            var shardIds = line![7..].Split(',').Select(int.Parse).ToArray();
            Assert.Equal(2, shardIds.Length);
            Assert.NotEqual(shardIds[0], shardIds[1]);
            using var shard0 = Process.GetProcessById(shardIds[0]);
            using var shard1 = Process.GetProcessById(shardIds[1]);
            var shards = new[] { shard0, shard1 };
            Assert.All(shards, process => Assert.False(process.HasExited));
            Assert.False(DurableAcceptanceJob.Observe(jobName, identity,
                SpawnTrackedProcessStatus.LiveMatch, cancel: false).Settled);

            holder.StandardInput.WriteLine("exit-holder");
            holder.StandardInput.Flush();
            Assert.True(holder.WaitForExit(30_000), "Holder did not exit after releasing its stdin gate.");
            // No conductor/holder handle remains. The independent custodian must keep the job
            // observable without assigning itself to that job or relying on the two shards' root.
            job.Dispose();
            Assert.All(shards, process => Assert.False(process.HasExited));
            // Fresh OpenJobObject calls model a successor; the root is absent but two shards remain.
            var successorObservation = DurableAcceptanceJob.Observe(jobName, identity,
                SpawnTrackedProcessStatus.DeadOrRecycled, cancel: false);
            Assert.False(successorObservation.Settled, successorObservation.Detail);
            // Job accounting includes every member, including any auxiliary processes.
            // Prove containment of both gated shards by identity instead of equating the
            // job's total process count with its shard count.
            using (var successorJob = OpenJobObjectW(0x0004, false, jobName)) // QUERY
            {
                Assert.False(successorJob.IsInvalid);
                Assert.True(OwnedProcessGroup.WindowsJob.TryGetActiveProcessIds(successorJob, out var members));
                Assert.All(shardIds, pid => Assert.Contains(pid, members));
                Assert.DoesNotContain(holder.Id, members);
                Assert.DoesNotContain(job.CustodianIdentity.ProcessId, members);
                Assert.DoesNotContain(Environment.ProcessId, members);
            }
            Assert.False(successorObservation.HolderMatches);
            Assert.False(done.WaitOne(0));
            Assert.All(shards, process => Assert.False(process.HasExited));
            var cancelled = DurableAcceptanceJob.Observe(jobName, identity,
                SpawnTrackedProcessStatus.DeadOrRecycled, cancel: true);
            Assert.True(cancelled.Settled, cancelled.Detail);
            Assert.All(shards, process => Assert.True(process.WaitForExit(30_000),
                $"Shard {process.Id} did not exit after cancellation of its named job."));
            // The Acceptance test host is an unrelated process and remains alive.
            using var self = Process.GetCurrentProcess();
            Assert.False(self.HasExited);
        }
        finally
        {
            done.Set();
            if (identity is not null)
                _ = DurableAcceptanceJob.Observe(jobName, identity,
                    holder.HasExited ? SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch,
                    cancel: true);
            // An assignment/identity-read failure occurs before the stdin gate is released.
            StopHolderIfSame(holder, identity);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenJobObjectW(uint access, bool inherit, string name);

    [Fact]
    public void MissingJobNameAndDeadHolderDoNotProveSettlement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var self = Process.GetCurrentProcess();
        var identity = SpawnProcessIdentityReader.ReadForRegistration(self).Identity!;
        var observation = DurableAcceptanceJob.Observe($"Local\\acceptance-absent-{Guid.NewGuid():N}",
            identity, SpawnTrackedProcessStatus.DeadOrRecycled, cancel: false);
        Assert.False(observation.Settled, observation.Detail);
        Assert.Equal("job-open-error:2", observation.Detail);
        Assert.False(self.HasExited);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("holder-null")]
    [InlineData("job")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("image")]
    [InlineData("malformed")]
    public void AbsentJobRequiresSettlementReceiptBoundToExactHolder(string field)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var self = Process.GetCurrentProcess();
        var identity = SpawnProcessIdentityReader.ReadForRegistration(self).Identity!;
        var name = $"Local\\acceptance-absent-{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetTempPath(), $"acceptance-settlement-{Guid.NewGuid():N}.json");
        var receiptHolder = field switch
        {
            "pid" => identity with { ProcessId = identity.ProcessId + 1 },
            "start" => identity with { StartedAt = identity.StartedAt.AddSeconds(1) },
            "image" => identity with { ImagePath = "other.exe" },
            _ => identity
        };
        try
        {
            var content = field == "malformed" ? "{not-json" : field == "holder-null"
                ? JsonSerializer.Serialize(new { JobName = name, Holder = (SpawnProcessIdentity?)null, Settled = true })
                : JsonSerializer.Serialize(new AcceptanceJobSettlement(field == "job" ? name + "-other" : name,
                    receiptHolder, true));
            File.WriteAllText(path, content);
            var observation = DurableAcceptanceJob.Observe(name, identity,
                SpawnTrackedProcessStatus.DeadOrRecycled, cancel: true, settlementPath: path);
            Assert.Equal(field == "matching", observation.Settled);
            Assert.False(self.HasExited);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CustodianPublishesBoundSettlementAfterNaturalHolderExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CustodyFixture();
        var path = Path.Combine(Path.GetTempPath(), $"acceptance-settlement-{Guid.NewGuid():N}.json");
        var name = $"Local\\acceptance-natural-{Guid.NewGuid():N}";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        var script = "[Console]::WriteLine('ready');[void][Console]::ReadLine();";
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) startInfo.ArgumentList.Add(arg);
        using var holder = ProcessTreeGuiSuppression.Start(startInfo)!;
        var identity = SpawnProcessIdentityReader.ReadForRegistration(holder).Identity!;
        try
        {
            using var job = DurableAcceptanceJob.Attach(holder, name, fixture.Launch, path);
            // Retain the exact custodian process handle before the root can exit.
            var custodian = fixture.TrackCustodian(job.CustodianIdentity);
            // A PID lookup alone does not retain the handle required by ExitCode after exit.
            Assert.False(custodian.SafeHandle.IsInvalid);
            Assert.Equal(job.CustodianIdentity.StartedAt, new DateTimeOffset(custodian.StartTime.ToUniversalTime()));
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.False(File.Exists(path));
            holder.StandardInput.WriteLine("exit");
            holder.StandardInput.Flush();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            job.Dispose();
            await custodian.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, custodian.ExitCode);
            var receipt = JsonSerializer.Deserialize<AcceptanceJobSettlement>(File.ReadAllText(path));
            Assert.Equal(new AcceptanceJobSettlement(name, identity, true), receipt);
            var observation = DurableAcceptanceJob.Observe(name, identity,
                SpawnTrackedProcessStatus.DeadOrRecycled, cancel: false, settlementPath: path);
            Assert.True(observation.Settled, observation.Detail);
            Assert.Equal("job-settlement-receipt", observation.Detail);
        }
        finally
        {
            StopHolderIfSame(holder, identity);
            _ = DurableAcceptanceJob.Observe(name, identity, SpawnTrackedProcessStatus.DeadOrRecycled,
                cancel: true, settlementPath: path);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustodyLossOrCancellationTerminatesOnlyOwnedJob(bool loseCustody)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CustodyFixture();
        using var holder = StartGatedHolder();
        using var healthy = StartGatedHolder();
        var identity = SpawnProcessIdentityReader.ReadForRegistration(holder).Identity!;
        var healthyIdentity = SpawnProcessIdentityReader.ReadForRegistration(healthy).Identity!;
        var name = $"Local\\custody-loss-{Guid.NewGuid():N}";
        var healthyName = $"Local\\custody-healthy-{Guid.NewGuid():N}";
        try
        {
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal("ready", await healthy.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            using var job = DurableAcceptanceJob.Attach(holder, name, fixture.Launch);
            var custody = fixture.TrackCustodian(job.CustodianIdentity);
            using var other = DurableAcceptanceJob.Attach(healthy, healthyName, fixture.Launch);
            fixture.TrackCustodian(other.CustodianIdentity);
            if (loseCustody) StopHolderIfSame(custody, job.CustodianIdentity);
            else Assert.True(DurableAcceptanceJob.Observe(name, identity,
                SpawnTrackedProcessStatus.LiveMatch, cancel: true).Settled);
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(holder.HasExited);
            var unrelated = DurableAcceptanceJob.Observe(healthyName, healthyIdentity,
                SpawnTrackedProcessStatus.LiveMatch, cancel: false);
            Assert.True(unrelated.HolderMatches, unrelated.Detail);
            Assert.False(unrelated.Settled, unrelated.Detail);
            Assert.False(healthy.HasExited);
        }
        finally
        {
            _ = DurableAcceptanceJob.Observe(name, identity, holder.HasExited ?
                SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch, cancel: true);
            _ = DurableAcceptanceJob.Observe(healthyName, healthyIdentity, healthy.HasExited ?
                SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch, cancel: true);
            StopHolderIfSame(holder, identity);
            StopHolderIfSame(healthy, healthyIdentity);
        }
    }

    [Fact]
    public async Task ReadinessPrecedesAssignmentAndEofCannotPublishSettlement()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CustodyFixture();
        using var holder = StartGatedHolder();
        var identity = SpawnProcessIdentityReader.ReadForRegistration(holder).Identity!;
        var name = $"Local\\custody-ready-{Guid.NewGuid():N}";
        var path = Path.Combine(fixture.Root, "settlement.json");
        using var job = OwnedProcessGroup.WindowsJob.CreateKillOnCloseJob(allowBreakaway: false, name: name);
        using var host = ProcessTreeGuiSuppression.Start(fixture.Launch.CreateStartInfo())!;
        var error = host.StandardError.ReadToEndAsync();
        try
        {
            var request = new AcceptanceJobCustodianProtocol("request", name, identity, null, path);
            host.StandardInput.WriteLine(JsonSerializer.Serialize(request));
            host.StandardInput.Flush();
            var ready = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var custodyIdentity = SpawnProcessIdentityReader.ReadForRegistration(host).Identity!;
            AcceptanceJobCustodianProtocol.Validate(ready, "ready", request, custodyIdentity);
            Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(custodyIdentity.ImagePath));
            Assert.True(OwnedProcessGroup.WindowsJob.TryGetActiveProcessCount(job, out var active));
            Assert.Equal(0u, active);
            Assert.False(holder.HasExited);
            host.StandardInput.Close();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, host.ExitCode);
            Assert.Contains("protocol identity mismatch", await error);
            Assert.False(File.Exists(path));
            Assert.False(holder.HasExited);
        }
        finally
        {
            if (!host.HasExited) host.Kill();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            StopHolderIfSame(holder, identity);
        }
    }

    [Fact]
    public async Task FailedWorkspaceStartupDoesNotAssignHolderOrRunGate()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CustodyFixture();
        using var holder = StartGatedHolder();
        var identity = SpawnProcessIdentityReader.ReadForRegistration(holder).Identity!;
        var name = $"Local\\custody-failed-{Guid.NewGuid():N}";
        try
        {
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var workspace = fixture.Workspace with { OrchestratorDirectory = Path.Combine(fixture.Root, "wrong-state") };
            var failure = Assert.Throws<InvalidOperationException>(() =>
                DurableAcceptanceJob.Attach(holder, name, AcceptanceJobCustodianHost.Bind(workspace)));
            Assert.Contains("differs from expected", failure.Message);
            using var job = OpenJobObjectW(0x0004, false, name);
            Assert.True(job.IsInvalid);
            Assert.False(holder.HasExited);
        }
        finally { StopHolderIfSame(holder, identity); }
    }

    private static Process StartGatedHolder()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::WriteLine('ready');[void][Console]::ReadLine()" }) start.ArgumentList.Add(arg);
        return ProcessTreeGuiSuppression.Start(start)!;
    }

    private sealed class CustodyFixture : IDisposable
    {
        private readonly List<Process> _custodians = [];
        internal string Root { get; } = Directory.CreateTempSubdirectory("native-custody-").FullName;
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(Root, Root, null,
            new OrchestratorProjectRegistry(Path.Combine(Root, "registry"), Path.Combine(Root, "data")));
        internal AcceptanceJobCustodianLaunchSpecification Launch => AcceptanceJobCustodianHost.Bind(Workspace);
        internal Process TrackCustodian(SpawnProcessIdentity identity)
        {
            var process = Process.GetProcessById(identity.ProcessId);
            try
            {
                _ = process.SafeHandle;
                _ = process.ProcessName;
                Assert.True(SpawnProcessIdentityReader.TryRead(process, out var live));
                Assert.Equal(SpawnTrackedProcessStatus.LiveMatch,
                    SpawnProcessIdentityReader.EvaluateRecordedIdentity(identity, live, out _));
                _custodians.Add(process);
                return process;
            }
            catch { process.Dispose(); throw; }
        }

        public void Dispose()
        {
            // An empty job can be observed before its independent custodian exits.
            // Wait on each recorded process before deleting its working directory.
            try
            {
                foreach (var process in _custodians)
                    Assert.True(process.WaitForExit(30_000), "Custodian did not exit after its owned job settled.");
                BoundedDirectoryRemoval.Remove(Root, _custodians);
            }
            finally { foreach (var process in _custodians) process.Dispose(); }
        }
    }

    private static void StopHolderIfSame(Process holder, SpawnProcessIdentity? identity)
    {
        if (holder.HasExited) return;
        Assert.NotNull(identity);
        Assert.True(SpawnProcessIdentityReader.TryRead(holder, out var liveIdentity),
            "Cannot verify the holder identity before test cleanup.");
        var status = SpawnProcessIdentityReader.EvaluateRecordedIdentity(identity, liveIdentity, out var evidence);
        Assert.True(status == SpawnTrackedProcessStatus.LiveMatch, evidence);
        holder.Kill();
        Assert.True(holder.WaitForExit(5000), "Holder did not exit after identity-validated test cleanup.");
    }
}
