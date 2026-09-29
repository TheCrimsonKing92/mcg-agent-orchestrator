using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorGroupedGateAttemptHost
{
    internal static int Run(string metadataPath)
    {
        ConductorGroupedGateAttempt? attempt = null;
        try
        {
            attempt = ConductorGroupedGateAttemptCoordinator.Read(metadataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.StdoutPath)!);
            using var stdout = new StreamWriter(new FileStream(attempt.StdoutPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            using var stderr = new StreamWriter(new FileStream(attempt.StderrPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
            using var writerLease = StorageRetentionMaintenance.AcquireAttemptWriterLease(
                Path.GetDirectoryName(metadataPath)!);
            using var self = Process.GetCurrentProcess();
            // Parent metadata may still contain pid 0 if a relaunch interrupts its post-spawn write.
            attempt = ConductorGroupedGateAttemptCoordinator.TryClaimOwner(
                metadataPath, Environment.ProcessId, self.StartTime.ToUniversalTime(),
                Environment.ProcessPath);
            if (attempt is null) return 0;
            var workspace = OrchestratorWorkspace.ForDirectory(
                attempt.ExecutionDirectory, attempt.ExecutionDirectory);
            var state = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var kernel = state.LoadGoalsAsync(attempt.Members
                .Select(member => new GoalId(member.GoalId)).ToArray()).GetAwaiter().GetResult();
            var providers = ProviderRegistryFactory.CreateDefaultProviders();
            var agentFallback = ProviderRegistryFactory.IsLlamaCppReachable()
                ? AgentCatalog.LlamaCppDefault() : null;
            var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
            var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
            var driver = new ConductorDriver(kernel, workspace,
                new GoalAcceptanceVerifier(DotnetBuildEnvironmentManager.CaptureStorageRoot(),
                    workspace.OrchestratorDirectory), agents, profiles,
                NullOperatorChannel.Instance, providers,
                cleanupHooks: WorktreeCleanupContext.Load(
                    attentionStoreDirectory: workspace.OrchestratorDirectory).Hooks);
            driver.RunGroupedGateAttemptBody(attempt);
            File.WriteAllText(attempt.ResultPath,
                JsonSerializer.Serialize(new { attempt.IdentityValue, Status = "completed" }));
            File.WriteAllText(attempt.ExitCodePath, "0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (attempt is not null)
            {
                File.WriteAllText(attempt.ResultPath,
                    JsonSerializer.Serialize(new { attempt.IdentityValue, Status = "failed", Error = ex.Message }));
                File.WriteAllText(attempt.ExitCodePath, "1");
            }
            return 1;
        }
    }
}
