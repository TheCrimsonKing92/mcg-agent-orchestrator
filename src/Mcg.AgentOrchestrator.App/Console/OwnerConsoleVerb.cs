using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleVerb
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("console", StringComparison.OrdinalIgnoreCase);

    internal static Task<int> RunAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        if (args.Skip(1).Any(arg => !arg.Equals("--plain", StringComparison.OrdinalIgnoreCase)))
        {
            System.Console.Error.WriteLine("Usage: console [--plain]");
            return Task.FromResult(1);
        }
        return OwnerConsoleModeSelector.RunAsync(args, System.Console.IsInputRedirected, System.Console.IsOutputRedirected,
            new(token => RunPlainAsync(workspace, token), token => OwnerConsoleFullScreenHost.RunAsync(workspace, token)), cancellationToken);
    }

    private static async Task<int> RunPlainAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken)
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
                new SystemConsoleOutput(), clock,
                new CliConductorConsoleAdapter(workspace),
                new CliOwnerDigestConsoleAdapter(workspace));
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
