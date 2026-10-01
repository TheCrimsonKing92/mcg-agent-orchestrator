using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorGroupedGateAttemptHost
{
    internal static int Run(string metadataPath) => Run(metadataPath, RunGateBody, RedirectConsole);

    internal static int Run(string metadataPath, Action<ConductorGroupedGateAttempt> gateBody,
        Func<TextWriter, TextWriter, IDisposable> redirectConsole)
    {
        var processError = Console.Error;
        ConductorGroupedGateAttempt? attempt = null;
        StreamWriter? stdout = null;
        StreamWriter? stderr = null;
        IDisposable? redirection = null;
        var exitCode = 0;
        try
        {
            attempt = ConductorGroupedGateAttemptCoordinator.Read(metadataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.StdoutPath)!);
            stdout = OpenWriter(attempt.StdoutPath);
            stderr = OpenWriter(attempt.StderrPath);
            redirection = redirectConsole(stdout, stderr);
            using var writerLease = StorageRetentionMaintenance.AcquireAttemptWriterLease(
                Path.GetDirectoryName(metadataPath)!);
            using var self = Process.GetCurrentProcess();
            // Parent metadata may still contain pid 0 if a relaunch interrupts its post-spawn write.
            var claimed = ConductorGroupedGateAttemptCoordinator.TryClaimOwner(
                metadataPath, Environment.ProcessId, self.StartTime.ToUniversalTime(),
                Environment.ProcessPath);
            if (claimed is not null)
            {
                attempt = claimed;
                gateBody(attempt);
                File.WriteAllText(attempt.ResultPath,
                    JsonSerializer.Serialize(new { attempt.IdentityValue, Status = "completed" }));
                File.WriteAllText(attempt.ExitCodePath, "0");
            }
        }
        catch (Exception ex)
        {
            if (attempt is null) BestEffort(() => processError.WriteLine(ex), processError);
            else PublishFailure(attempt, ex, stderr, processError);
            exitCode = 1;
        }
        finally
        {
            Cleanup(() => redirection?.Dispose());
            Cleanup(() => stdout?.Dispose());
            var errorWriter = stderr;
            stderr = null;
            Cleanup(() => errorWriter?.Dispose());
        }
        return exitCode;

        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception)
            {
                if (exitCode == 0 && attempt is not null)
                    PublishFailure(attempt, exception, stderr, processError);
                else BestEffort(() => processError.WriteLine(exception), processError);
                exitCode = 1;
            }
        }
    }

    private static void RunGateBody(ConductorGroupedGateAttempt attempt)
    {
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
    }

    private static StreamWriter OpenWriter(string path) => new(new FileStream(path,
        FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

    private static void PublishFailure(ConductorGroupedGateAttempt attempt, Exception exception,
        TextWriter? stderr, TextWriter processError)
    {
        BestEffort(() =>
        {
            if (stderr is not null) stderr.WriteLine(exception);
            else
            {
                using var writer = OpenWriter(attempt.StderrPath);
                writer.WriteLine(exception);
            }
        }, processError);
        BestEffort(() => File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new
        {
            attempt.IdentityValue, Status = "failed", ErrorType = exception.GetType().Name,
            Error = exception.Message
        })), processError);
        BestEffort(() => File.WriteAllText(attempt.ExitCodePath, "1"), processError);
    }

    private static void BestEffort(Action action, TextWriter processError)
    {
        try { action(); }
        catch (Exception exception)
        {
            try { processError.WriteLine(exception); }
            catch (Exception) { } // Reporting must never mask the original failure.
        }
    }

    private static IDisposable RedirectConsole(TextWriter stdout, TextWriter stderr)
    {
        var restoration = new ConsoleRestoration(Console.Out, Console.Error);
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            return restoration;
        }
        catch
        {
            restoration.Dispose();
            throw;
        }
    }

    private sealed class ConsoleRestoration(TextWriter stdout, TextWriter stderr) : IDisposable
    {
        public void Dispose()
        {
            try { Console.SetOut(stdout); }
            finally { Console.SetError(stderr); }
        }
    }
}
