using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAttentionJsonQueryCommand
{
    internal static bool IsAttentionJsonCommand(IReadOnlyList<string> args) =>
        args.Count is 2 or 3 && args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        (args.Count == 2 && args[1] == "--json" ||
         args.Count == 3 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase) && args[2] == "--json") &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        var context = AttentionJsonReadContext.ReadAsync(stateQueries, workspace,
            (time ?? TimeProvider.System).GetUtcNow(), cancellationToken).GetAwaiter().GetResult();
        Console.WriteLine(AttentionAgentReadView.Render(context.Waits, context.Items, time,
            context.ProviderHolds, gateEvidence: context.GateEvidence, ownerQuestions: context.OwnerQuestions));
    }
}
