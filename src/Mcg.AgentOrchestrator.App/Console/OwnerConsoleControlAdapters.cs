using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class CliConductorConsoleAdapter(OrchestratorWorkspace workspace) : IOwnerConsoleConductor
{
    public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) =>
        CliConductorCommand.Run(args, workspace, output: output, error: error);
}

internal sealed class CliOwnerDigestConsoleAdapter(OrchestratorWorkspace workspace) : IOwnerConsoleDigestReport
{
    public int Run(TextWriter output) =>
        CliOwnerDigestCommand.Run(["owner-digest"], workspace, output: output);
}
