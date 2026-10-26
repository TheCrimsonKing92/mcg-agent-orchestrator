using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliMonitorGoalQueryCommand
{
    internal const string WriterPathRefusal = "monitor-goal and goals subscribe are served only by the read-only query route.";

    internal static bool IsMonitorGoalQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        return args[0].Equals("monitor-goal", StringComparison.OrdinalIgnoreCase) ||
            (args.Count >= 2 && args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) &&
             args[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase));
    }

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        IClock? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TextWriter? output = null)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var options = GoalMonitoringSubscriptionCommand.Parse(args);
        if (string.IsNullOrWhiteSpace(options.GoalId))
            throw new ArgumentException(GoalMonitoringSubscriptionCommand.Usage);

        AgentOrchestratorKernel Reload()
        {
            var statuses = stateQueries.ListGoalIdStatusesAsync().GetAwaiter().GetResult();
            var selectedIds = statuses.Where(goal =>
                    goal.Id.StartsWith(options.GoalId, StringComparison.OrdinalIgnoreCase) ||
                    !Enum.TryParse<GoalStatus>(goal.Status, ignoreCase: true, out var status) ||
                    status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
                .Select(goal => new GoalId(goal.Id)).Distinct().ToArray();
            var snapshots = selectedIds.Select(id =>
                stateQueries.LoadGoalsAsync([id]).GetAwaiter().GetResult().ExportSnapshot()).ToArray();
            var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
                snapshots.SelectMany(snapshot => snapshot.Goals).ToList(),
                snapshots.SelectMany(snapshot => snapshot.HumanInputRequests).ToList()), clock);
            kernel.MarkKnownDependencyGoalStatuses(statuses.Select(goal =>
                new KeyValuePair<GoalId, string>(new GoalId(goal.Id), goal.Status)));
            return kernel;
        }

        GoalMonitoringSubscriptionCommand.RunAsync(
            args, output ?? Console.Out, new AgentOrchestratorKernel(clock), workspace, agents, workerProfiles,
            reloadKernel: Reload, clock: clock, delay: delay,
            openRunEvents: () => File.Exists(workspace.RunEventStorePath)
                ? new SqliteRunEventStore(workspace.RunEventStorePath, ensureSchema: false)
                : null).GetAwaiter().GetResult();
    }
}
