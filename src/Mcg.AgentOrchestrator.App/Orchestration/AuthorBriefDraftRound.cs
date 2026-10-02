using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorBriefDraftSeams(
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> RunProcessAsync,
    IAuthorBriefDraftRepository Repository);

internal static class AuthorBriefDraftRound
{
    internal static Task<WorkerProcessRunResult> DispatchAsync(string prompt, string repositoryRoot,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runProcessAsync,
        CancellationToken cancellationToken = default) =>
        runProcessAsync(new WorkerProcessRunRequest("claude --model sonnet --permission-mode plan -p",
            repositoryRoot, TimeSpan.FromMinutes(10), prompt), cancellationToken);
}
