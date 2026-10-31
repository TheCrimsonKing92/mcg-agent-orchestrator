using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStartLaunchPlan
{
    internal static ConductorLaunchRequest Create(
        OrchestratorWorkspace workspace, WorkspaceInvocationBinding? binding)
    {
        // Use the owning binding verifier before writing any launcher artifacts.
        new WorkspaceInvocationBinding(workspace.RootDirectory, workspace.OrchestratorDirectory,
            binding?.DisposableFixture == true, []).Verify(workspace);
        var appDll = typeof(OrchestratorWorkspace).Assembly.Location;
        List<string> arguments = ["conduct", "--loop", "--daemon", "--watch",
            "--poll-seconds", "120", "--max-duration", "43200",
            $"--project={workspace.ProjectName}", $"--tenant={workspace.TenantName}",
            "--repository-root", workspace.RootDirectory,
            "--expected-state-directory", workspace.OrchestratorDirectory];
        if (binding?.DisposableFixture == true) arguments.Add("--disposable-fixture");

        var logRoot = Path.Combine(workspace.OrchestratorDirectory, "logs");
        Directory.CreateDirectory(logRoot);
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var stdoutPath = Path.Combine(logRoot, $"operator-conduct-loop-daemon-{stamp}.out.log");
        var stderrPath = Path.Combine(logRoot, $"operator-conduct-loop-daemon-{stamp}.err.log");
        // Keep the journal consumed by Resume-OrchestratorLoop.ps1 in the script's schema.
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "last-drive.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                writtenAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                repoRoot = workspace.ExecutionDirectory,
                name = "conduct-loop-daemon",
                appDll,
                arguments,
                policy = (string?)null,
                pollSeconds = "120",
                watchInterval = (string?)null,
                maxDuration = "43200",
                maxIterations = (string?)null,
                watch = true,
                daemon = true
            }, new JsonSerializerOptions { WriteIndented = true }));

        var host = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH");
        return new ConductorLaunchRequest(string.IsNullOrWhiteSpace(host) ? "dotnet" : host,
            [appDll, .. arguments], workspace.ExecutionDirectory,
            new Dictionary<string, string>
            {
                ["MCG_DISPATCH_MAX_RUNTIME_MIN"] = "120",
                ["MCG_ORCHESTRATOR_STDOUT_LOG_PATH"] = stdoutPath,
                ["MCG_ORCHESTRATOR_STDERR_LOG_PATH"] = stderrPath,
                ["MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME"] = "conduct-loop-daemon"
            },
            [CliProtectedProcessEnvironment.ProtectedPidVariable,
                CliProtectedProcessEnvironment.ProtectedStartTicksVariable], stdoutPath, stderrPath);
    }
}
