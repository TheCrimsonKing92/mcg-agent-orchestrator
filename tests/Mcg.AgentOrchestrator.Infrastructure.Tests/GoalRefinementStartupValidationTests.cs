using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

public sealed class GoalRefinementStartupValidationTests
{
    [Fact]
    public async Task WhitespaceGoalIdReportsUsageBeforeCreatingStateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-refinement-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var invalidGoalId = "1234567890abcdef1234567890abcdef stamp";
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
            startInfo.ArgumentList.Add("goal-refinement-run");
            startInfo.ArgumentList.Add(invalidGoalId);
            startInfo.ArgumentList.Add("stamp");
            startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = root;
            startInfo.Environment[Mcg.AgentOrchestrator.Infrastructure.DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
                Path.Combine(root, ".orchestrator", "test-dotnet");

            var result = await CliChildProcessRunner.RunAsync(startInfo);
            var standardOutput = result.StandardOutput;
            var standardError = result.StandardError;

            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(standardOutput);
            Assert.Contains("Usage: goal-refinement-run", standardError, StringComparison.Ordinal);
            Assert.Contains($"<goal-id> argument '{invalidGoalId}' contains whitespace", standardError, StringComparison.Ordinal);
            Assert.DoesNotContain("claim-miss", standardError, StringComparison.Ordinal);
            Assert.False(File.Exists(workspace.SqliteStatePath), "Invalid arguments must be rejected before the state store is initialized.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
