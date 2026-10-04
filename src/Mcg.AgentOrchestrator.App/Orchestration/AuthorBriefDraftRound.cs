using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorBriefDraftSeams(
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> RunProcessAsync,
    IAuthorBriefDraftRepository Repository,
    ModelFunctionCatalog? Catalog = null);

internal static class AuthorBriefDraftRound
{
    internal static Task<WorkerProcessRunResult> DispatchAsync(string prompt, string repositoryRoot,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runProcessAsync,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(prompt, repositoryRoot, runProcessAsync, ModelFunctionCatalog.Empty, cancellationToken);

    internal static Task<WorkerProcessRunResult> DispatchAsync(string prompt, string repositoryRoot,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runProcessAsync,
        ModelFunctionCatalog? catalog, CancellationToken cancellationToken = default) =>
        DispatchAsync(prompt, repositoryRoot, runProcessAsync,
            ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.ConductorAuthor), cancellationToken);

    internal static Task<WorkerProcessRunResult> DispatchAsync(string prompt, string repositoryRoot,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runProcessAsync,
        ConductorRoundModel model, CancellationToken cancellationToken = default)
    {
        if (!model.IsValid) throw new ConductorModelRoundException(model.InvalidReason!, null);
        return runProcessAsync(new WorkerProcessRunRequest($"claude {model.ModelArguments} --permission-mode plan -p",
            repositoryRoot, TimeSpan.FromMinutes(10), prompt), cancellationToken);
    }
}
