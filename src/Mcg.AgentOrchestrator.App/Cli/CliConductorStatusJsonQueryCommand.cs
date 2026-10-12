namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliConductorStatusJsonQueryCommand
{
    internal static bool IsConductorStatusJsonCommand(IReadOnlyList<string> args) =>
        args.Count == 3 && args[0].Equals("conductor", StringComparison.OrdinalIgnoreCase) &&
        args[1].Equals("status", StringComparison.OrdinalIgnoreCase) && args[2] == "--json" &&
        !CliCommandHelp.IsCommandSpecificHelp(args);
}
