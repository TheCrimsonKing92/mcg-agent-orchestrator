using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAuthorDraftCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("author-draft", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Run(args, workspace, new(WorkerProcessRunner.RunBufferedAsync,
            new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory),
            ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath)), Console.Out, Console.Error);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        AuthorBriefDraftSeams seams, TextWriter output, TextWriter error)
    {
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            if (!IsCommand(args) || args.Count != 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
                throw new ArgumentException(CliCommandHelp.AuthorDraftUsage);
        }
        catch (Exception ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        return AuthorBriefDraftService.Run(args[1], workspace, seams, output, error).ExitCode;
    }
}
