using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool IsCancelDispatchCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(OperatorIntentVerbs.CancelDispatch, StringComparison.OrdinalIgnoreCase);
}
