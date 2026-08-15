using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public static class SharedTestSupport
{
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-orchestrator-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static object CreateRefinedWorkspaceOpaque(string root)
    {
        SeedLocalSkillCatalog(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        SeedSpecRefinerBinding(workspace);
        return workspace;
    }

    public static void SeedLocalSkillCatalog(string workingDirectory)
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), ".agents", "skills");
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "SKILL.md", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.Combine(workingDirectory, ".agents", "skills", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    public static SqliteOrchestratorStateRepository CreateMigratedStateRepository(
        string databasePath)
    {
        _ = StateDbMigrations.EnsureUpToDate(databasePath);
        return new SqliteOrchestratorStateRepository(databasePath);
    }

    public static void SeedSpecRefinerBinding(object workspace)
    {
        var orchestratorWorkspace = workspace as OrchestratorWorkspace
            ?? throw new ArgumentException("Expected an OrchestratorWorkspace.", nameof(workspace));
        ModelFunctionCatalogStore.Save(orchestratorWorkspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
    }

    public static Process StartPrototypeDashboardProcess(string appProject, string workingDirectory, string url) =>
        StartDashboardProcess(appProject, workingDirectory, "prototype-ui", url);

    public static Process StartDashboardProcess(string appProject, string workingDirectory, string command, string url)
    {
        var appAssembly = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Pin Ollama to an unreachable endpoint so assertions are deterministic
        // regardless of whether a live Ollama server runs on this machine.
        startInfo.EnvironmentVariables["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        startInfo.EnvironmentVariables[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;

        // Spawned-app dispatch starts must never launch real subscription CLIs:
        // codex/claude authenticate from account state, not the API keys pinned
        // below, so without this flag auto-handoff tests burn real usage and edit
        // real worktrees under the repository root.
        startInfo.EnvironmentVariables[BackgroundDispatchRunner.DisableDispatchStartVariable] = "1";

        // Pin provider credentials and model names so spawned-app assertions are
        // machine-independent regardless of what keys or models the host has set.
        startInfo.EnvironmentVariables["OPENAI_API_KEY"] = "test-openai-key";
        startInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] = "test-anthropic-key";
        startInfo.EnvironmentVariables["OPENAI_MODEL"] = "test-openai-model";
        startInfo.EnvironmentVariables["ANTHROPIC_MODEL"] = "test-anthropic-model";
        startInfo.EnvironmentVariables["OLLAMA_MODEL"] = "test-ollama-model";

        startInfo.ArgumentList.Add(appAssembly);
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(url);
        startInfo.ArgumentList.Add("--no-open");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start prototype dashboard process.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public static async Task WaitForHealthAsync(HttpClient client, string url, Process process)
    {
        var health = new Uri(new Uri(url), "health");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Dashboard exited early with code {process.ExitCode}.");
            }

            try
            {
                var response = await client.GetAsync(health);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Dashboard health endpoint did not respond at {health}. Last error: {lastError?.Message}");
    }

    public static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string FindRepositoryRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable)
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

}

public sealed class FakeSmokeProvider : IModelProvider
{
    private readonly string _providerName;
    private readonly string _text;
    private readonly ModelUsage? _usage;
    private readonly string _stopReason;

    public FakeSmokeProvider(string text = "OK", ModelUsage? usage = null, string stopReason = "stop", string providerName = "Fake")
    {
        _providerName = providerName;
        _text = text;
        _usage = usage ?? new ModelUsage(1, 2);
        _stopReason = stopReason;
    }

    public string ProviderName => _providerName;

    public ModelRequest? LastRequest { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new ModelResponse(_text, _usage, _stopReason));
    }
}
