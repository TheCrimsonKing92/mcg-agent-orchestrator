using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAttentionJsonQueryCommand
{
    internal static bool IsAttentionJsonCommand(IReadOnlyList<string> args) =>
        args.Count is 2 or 3 or 4 && args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        (args.Count == 2 && args[1] == "--json" ||
         args.Count == 3 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase) && args[2] == "--json" ||
         IsSummaryCommand(args)) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    private static bool IsSummaryCommand(IReadOnlyList<string> args) =>
        args.Count == 4 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase) &&
        (args[2] == "--summary" && args[3] == "--json" || args[2] == "--json" && args[3] == "--summary");

    internal static void Execute(IReadOnlyList<string> args, IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        var summary = IsSummaryCommand(args);
        var context = AttentionJsonReadContext.ReadAsync(stateQueries, workspace,
            (time ?? TimeProvider.System).GetUtcNow(), new AttentionProjectionReadOptions(!summary),
            cancellationToken).GetAwaiter().GetResult();
        Console.WriteLine(summary
            ? AttentionAgentReadView.RenderSummary(context.Waits, context.Items, time,
                context.ProviderHolds, context.OwnerQuestions, context.PostLandingDuties)
            : AttentionAgentReadView.Render(context.Waits, context.Items, time,
                context.ProviderHolds, gateEvidence: context.GateEvidence, ownerQuestions: context.OwnerQuestions,
                postLandingDuties: context.PostLandingDuties));
    }
}
