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

    // Total time a temp-directory removal keeps retrying before it fails loudly.
    public const int RemoveTempDirectoryBudgetMilliseconds = 10_000;

    // Backoff for the first retry; it doubles after each failed attempt.
    public const int RemoveTempDirectoryInitialBackoffMilliseconds = 50;

    // Ceiling the doubling backoff is clamped to.
    public const int RemoveTempDirectoryMaximumBackoffMilliseconds = 500;

    /// <summary>
    /// Removes a temp directory tree, retrying while another process still holds it and
    /// throwing loudly once the retry budget is spent. Returns silently when the directory
    /// is already gone or disappears while retrying.
    /// </summary>
    public static void RemoveTempDirectory(string path)
    {
        var stopwatch = Stopwatch.StartNew();
        RemoveTempDirectory(path, () => stopwatch.Elapsed, Thread.Sleep);
    }

    /// <summary>
    /// Seam overload: <paramref name="elapsed"/> reports time spent since the first attempt and
    /// <paramref name="delay"/> waits between attempts, so tests drive the retry loop without sleeping.
    /// </summary>
    public static void RemoveTempDirectory(
        string path,
        Func<TimeSpan> elapsed,
        Action<TimeSpan> delay,
        TimeSpan? budget = null,
        TimeSpan? initialBackoff = null,
        TimeSpan? maximumBackoff = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            // A blank path means the fixture never created its root; that is a test bug to
            // surface, not a cleanup to tolerate.
            throw new ArgumentException("A temp directory path is required.", nameof(path));
        }

        ArgumentNullException.ThrowIfNull(elapsed);
        ArgumentNullException.ThrowIfNull(delay);

        var totalBudget = budget ?? TimeSpan.FromMilliseconds(RemoveTempDirectoryBudgetMilliseconds);
        var backoff = initialBackoff ?? TimeSpan.FromMilliseconds(RemoveTempDirectoryInitialBackoffMilliseconds);
        var backoffCap = maximumBackoff ?? TimeSpan.FromMilliseconds(RemoveTempDirectoryMaximumBackoffMilliseconds);

        var attempts = 0;
        Exception lastError;
        while (true)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            attempts++;
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                // A holder that released by deleting the tree itself is the same end state.
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lastError = error;
            }

            if (!Directory.Exists(path))
            {
                return;
            }

            var spent = elapsed();
            if (spent >= totalBudget)
            {
                throw new IOException(
                    $"Failed to remove temp directory '{path}' after {attempts} attempts over "
                        + $"{spent.TotalMilliseconds:F0} ms. Last error: {lastError.Message}",
                    lastError);
            }

            delay(backoff);
            backoff = backoff >= backoffCap ? backoffCap : Min(backoff + backoff, backoffCap);
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

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
