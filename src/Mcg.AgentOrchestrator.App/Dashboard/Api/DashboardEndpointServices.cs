using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Extensions.Hosting;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record DashboardEndpointServices(
    DashboardStateService State,
    OrchestratorWorkspace Workspace,
    IModelProviderRegistry Providers,
    DashboardHostArgs HostArgs,
    IHostApplicationLifetime Lifetime,
    DashboardContinuationService Continuations,
    AgentCatalog? AgentCatalogFallback = null)
{
    public string AgentCatalogPath => Workspace.AgentCatalogPath;

    public string WorkerProfilePath => Workspace.WorkerProfilePath;

    public AgentCatalog LoadAgentCatalog() => AgentCatalogStore.Load(AgentCatalogPath, AgentCatalogFallback);
}

internal sealed class DashboardStateService(IOrchestratorStateRepository repository) : IDisposable
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private const int FileAccessAttempts = 3;

    public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadWithRetryAsync(cancellationToken);
    }

    public async Task<IResult> MutateAsync(
        Func<AgentOrchestratorKernel, Task<IResult>> mutation,
        CancellationToken cancellationToken = default)
    {
        return await MutateAsync(
            (kernel, _) => mutation(kernel),
            cancellationToken);
    }

    public async Task<IResult> MutateAsync(
        Func<AgentOrchestratorKernel, Func<Task>, Task<IResult>> mutation,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var kernel = await LoadWithRetryAsync(cancellationToken);
            var checkpointSaved = false;
            async Task SaveCheckpointAsync()
            {
                await SaveWithRetryAsync(kernel, cancellationToken);
                checkpointSaved = true;
            }

            var result = await mutation(kernel, SaveCheckpointAsync);
            try
            {
                await SaveWithRetryAsync(kernel, cancellationToken);
            }
            catch when (checkpointSaved)
            {
                return result;
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<IResult> MutateIfChangedAsync(
        Func<AgentOrchestratorKernel, Task<(bool Changed, IResult Result)>> mutation,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var kernel = await LoadWithRetryAsync(cancellationToken);
            var (changed, result) = await mutation(kernel);
            if (changed)
            {
                await SaveWithRetryAsync(kernel, cancellationToken);
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<T> MutateValueIfChangedAsync<T>(
        Func<AgentOrchestratorKernel, Task<(bool Changed, T Value)>> mutation,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var kernel = await LoadWithRetryAsync(cancellationToken);
            var (changed, value) = await mutation(kernel);
            if (changed)
            {
                await SaveWithRetryAsync(kernel, cancellationToken);
            }

            return value;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<OrchestratorStateRollbackResult> RestoreBackupAsync(
        string statePath,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await OrchestratorStateStore.RestoreBackupAsync(statePath, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        _mutationGate.Dispose();
    }

    private static bool IsTransientFileAccess(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }

    private async Task<AgentOrchestratorKernel> LoadWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await repository.LoadAsync(cancellationToken);
            }
            catch (Exception ex) when (attempt < FileAccessAttempts && IsTransientFileAccess(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    private async Task SaveWithRetryAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await repository.SaveAsync(kernel, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < FileAccessAttempts && IsTransientFileAccess(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }
}
