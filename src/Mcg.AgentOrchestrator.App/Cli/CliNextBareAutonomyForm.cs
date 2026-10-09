using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliNextBareAutonomyForm
{
    internal static bool IsForm(IReadOnlyList<string> args) =>
        args.Count == 3 && args[0].Equals("next", StringComparison.OrdinalIgnoreCase) &&
        (args[1].Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
         args[1].Equals("--autonomy-policy", StringComparison.OrdinalIgnoreCase)) &&
        !string.IsNullOrWhiteSpace(args[2]) && !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static IOrchestratorStateQueries ShareGoalMetadata(IOrchestratorStateQueries queries) =>
        new SharedGoalMetadataQueries(queries);

    // The selector and route share one listing for this invocation, including terminal creation times.
    private sealed class SharedGoalMetadataQueries(IOrchestratorStateQueries inner) : IOrchestratorStateQueries
    {
        private Task<IReadOnlyList<GoalSummary>>? _metadata;

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            CancellationToken cancellationToken = default) =>
            _metadata ??= inner.ListGoalMetadataAsync(includeTerminalCreatedAt: true, cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            bool includeTerminalCreatedAt, CancellationToken cancellationToken = default) =>
            ListGoalMetadataAsync(cancellationToken);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListOpenHumanInputRequestsAsync(cancellationToken);

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.ListTerminalOwnerQuestionHoldsAsync(goalIds, cancellationToken);
    }
}
