using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorSuccessorSelfCheck
{
    internal const string SubcommandName = "__conductor-successor-self-check";

    internal static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 7)
        {
            Console.Error.WriteLine(
                $"Usage: {SubcommandName} <repository-root> <state-store-path> <expected-git-head> " +
                "<agent-catalog-path> <worker-profile-path> <model-function-catalog-path>");
            return 2;
        }

        try
        {
            var repositoryRoot = Path.GetFullPath(args[1]);
            var expectedHead = args[3].Trim();
            var markerPath = Path.Combine(
                AppContext.BaseDirectory,
                "Mcg.AgentOrchestrator.App.dll.git-head");
            var builtHead = File.ReadAllText(markerPath).Trim();
            if (!string.Equals(builtHead, expectedHead, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Successor binary git HEAD mismatch: expected {expectedHead}, found {builtHead}.");
            }

            var schemaVersion = SqliteOrchestratorStateRepository.ValidateReadOnlySchema(args[2]);
            var agents = AgentCatalogStore.Load(args[4]).Agents;
            var profiles = WorkerProfileStore.Load(args[5]).Profiles;
            var modelFunctions = ModelFunctionCatalogStore.Load(args[6]).Bindings;
            var startupContract = GoalAcceptanceVerifier.ValidateStartupContract(repositoryRoot);
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            Console.WriteLine(
                $"LOOP_START selfCheck=true version={version} gitHead={builtHead} schemaVersion={schemaVersion} " +
                $"manifestChecks={startupContract.ManifestCheckCount} lanes={startupContract.InfrastructureLaneNames.Count} " +
                $"agents={agents.Count} profiles={profiles.Count} modelFunctions={modelFunctions.Count} " +
                $"runDir={Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)}");
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"LOOP_SELF_CHECK_FAILED exception={ex.GetType().Name} message={Sanitize(ex.Message)}");
            Console.Error.Flush();
            return 1;
        }
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
