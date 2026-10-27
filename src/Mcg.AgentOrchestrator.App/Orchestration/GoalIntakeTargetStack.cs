using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalIntakeTargetStack
{
    internal const string GoBuildTestTool = "go build ./..., go vet ./..., go test ./...";
    internal const string NeutralBuildTestTool = "the project's own build and test commands";

    internal static Toolchain? For(
        OrchestratorWorkspace workspace, Func<string, string?>? readEnvironment = null) =>
        OrchestratorHome.ResolveForLaunch(workspace.ExecutionDirectory, readEnvironment).IsHome(workspace)
            ? null
            : ProjectStackResolver.Resolve(workspace.ExecutionDirectory, workspace.ProjectHomeDirectoryOrNull);

    internal static string BuildTestTool(Toolchain stack) => stack switch
    {
        Toolchain.Go => GoBuildTestTool,
        _ => NeutralBuildTestTool
    };
}
