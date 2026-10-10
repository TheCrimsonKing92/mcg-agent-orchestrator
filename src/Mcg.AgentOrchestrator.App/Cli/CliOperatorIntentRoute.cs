using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Intent submission and observation own no kernel writes or worker lifecycle effects.
internal static class CliOperatorIntentRoute
{
    internal static IReadOnlySet<string> ServedVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "operator-intent-status",
        OperatorIntentVerbs.CriterionEvidenceMap
    };

    internal static bool IsServed(IReadOnlyList<string> args) =>
        args.Count > 0 && ServedVerbs.Contains(args[0]) && !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        Func<string, ITransactionalOrchestratorStateRepository>? openReadOnly = null)
    {
        try
        {
            if (!IsServed(args))
                throw new ArgumentException("Command is not served by the operator intent route.");
            if (args[0].Equals("operator-intent-status", StringComparison.OrdinalIgnoreCase))
            {
                CliCriterionEvidenceIntents.PrintStatus(args, workspace);
                return 0;
            }

            var selector = CliCriterionEvidenceIntents.ResolveFlagValue(args, "--goal")
                ?? throw new ArgumentException($"{args[0]} requires --goal <goal-prefix>.");
            var repository = (openReadOnly ?? SqliteOrchestratorStateRepository.OpenReadOnly)(workspace.SqliteStatePath);
            var matches = repository.ListGoalMetadataAsync().GetAwaiter().GetResult()
                .Where(goal => goal.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
            var goalId = matches.Length switch
            {
                1 => new GoalId(matches[0].Id),
                0 => throw new KeyNotFoundException($"Goal '{selector}' was not found."),
                _ => throw new InvalidOperationException($"Goal prefix '{selector}' is ambiguous.")
            };
            var snapshot = repository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
                ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
            var goal = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])).GetGoal(goalId);
            CliCriterionEvidenceIntents.Submit(args, workspace, goal,
                CliPersistentStateRunner.ResolveOperatorIntentAttribution(args, CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli));
            return 0;
        }
        catch (CliExitException ex)
        {
            return ex.ExitCode;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
            return 1;
        }
    }
}
