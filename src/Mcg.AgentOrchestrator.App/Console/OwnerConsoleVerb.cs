using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleVerb
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("console", StringComparison.OrdinalIgnoreCase);

    internal static async Task<int> RunAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken = default)
    {
        try
        {
            var clock = TimeProvider.System;
            IOrchestratorStateQueries state = File.Exists(workspace.SqliteStatePath)
                ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                : new EmptyStateQueries();
            var source = new ConductEventFileSource(workspace.ConductEventsLogPath, clock);
            var session = new OwnerConsoleSession(
                state,
                new OwnerQuestionReadModel(state, workspace.OrchestratorDirectory),
                new AttentionAnswerHandlerAdapter(workspace),
                new ConductorLeaseLiveness(workspace.OrchestratorDirectory),
                new OwnerDigestSummaryAdapter(workspace),
                new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory),
                new SystemConsoleOutput(), clock);
            await new OwnerConsoleLoop(session, new SystemConsoleInput(), source, clock)
                .RunAsync(cancellationToken);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private sealed class EmptyStateQueries : IOrchestratorStateQueries
    {
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>([]);

        public Task<Mcg.AgentOrchestrator.Core.AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<Mcg.AgentOrchestrator.Core.GoalId> ids,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Mcg.AgentOrchestrator.Core.AgentOrchestratorKernel());
    }
}
