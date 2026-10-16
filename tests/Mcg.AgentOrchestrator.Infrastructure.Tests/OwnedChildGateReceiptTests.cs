using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe Windows composition: one fixture-owned child, named job and artifact root.
public sealed class OwnedChildGateReceiptTests
{
    [Fact]
    public async Task ProductionGate_DetachWritesReceiptToWorkspaceConductLog()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = SharedTestSupport.CreateTempDirectory();
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::WriteLine('ready');[void][Console]::ReadLine()" }) start.ArgumentList.Add(arg);
        using var holder = ProcessTreeGuiSuppression.Start(start)!;
        var holderStopIdentity = TestOwnedProcessStop.Identify(holder);
        Process? custodian = null;
        try
        {
            Assert.NotNull(holderStopIdentity);
            Assert.Equal("ready", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var workspace = OrchestratorWorkspace.ForDirectory(root, root, null,
                new OrchestratorProjectRegistry(Path.Combine(root, "registry"), Path.Combine(root, "data")));
            var coordinator = OwnedChildAttemptCoordinatorFactory.CreateGate(workspace, null, (_, _) => null, false);
            var directory = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", "fixture-goal");
            Directory.CreateDirectory(directory);
            var identity = Assert.IsType<SpawnProcessIdentity>(
                SpawnProcessIdentityReader.ReadForRegistration(holder).Identity);
            var generation = AcceptanceHolderLease.CurrentGeneration();
            var attempt = new ConductorParallelAcceptanceAttempt("fixture-attempt", "fixture-goal", "fixture",
                0, "branch", "main", identity.StartedAt, identity.StartedAt, identity.ProcessId,
                ConductorParallelAcceptanceAttemptOutcome.Running,
                Path.Combine(directory, "out"), Path.Combine(directory, "err"),
                Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
                Path.Combine(directory, "result"), Path.Combine(directory, "fixture-attempt.attempt.json"))
            {
                HolderContractVersion = 1, OwnerProcessStartedAt = identity.StartedAt,
                OwnerExecutablePath = identity.ImagePath
            };
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var binding = AcceptanceHolderBinding.For(attempt);
            var lease = new AcceptanceHolderLease(attach: _ => new EmptyDisposable());
            lease.Reserve(binding, generation);
            using var job = DurableAcceptanceJob.Attach(holder, binding.JobName, binding.SettlementPath);
            custodian = Process.GetProcessById(job.CustodianIdentity.ProcessId);
            Assert.False(custodian.SafeHandle.IsInvalid);
            Assert.Equal(job.CustodianIdentity.StartedAt, new DateTimeOffset(custodian.StartTime.ToUniversalTime()));
            using var claim = lease.Claim(binding, identity);
            Assert.Equal("Claimed", lease.Read(binding).Phase);

            coordinator.PrepareHolderDetach();

            Assert.Equal("Detached", lease.Read(binding).Phase);
            var log = File.ReadAllText(workspace.ConductEventsLogPath);
            Assert.Contains("ACCEPTANCE_HOLDER_DETACH", log, StringComparison.Ordinal);
            Assert.Contains($"attempt={attempt.AttemptId}", log, StringComparison.Ordinal);
            Assert.Contains("slot=slot-0", log, StringComparison.Ordinal);
            holder.StandardInput.WriteLine("exit");
            holder.StandardInput.Flush();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!holder.HasExited)
            {
                TestOwnedProcessStop.StopTreeIfSame(holderStopIdentity);
                await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            if (custodian is not null)
            {
                using (custodian)
                    await custodian.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
}
