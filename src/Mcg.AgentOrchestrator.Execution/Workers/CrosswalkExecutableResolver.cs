using System.Security.Cryptography;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class CrosswalkExecutableResolver
{
    private readonly Lazy<CrosswalkExecutableResolution> _resolution;

    public CrosswalkExecutableResolver(string? explicitPath = null, string? dataRoot = null,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runVersionAsync = null,
        TimeSpan? versionTimeout = null)
    {
        _resolution = new(() => ResolveOnce(explicitPath, dataRoot,
            runVersionAsync ?? WorkerProcessRunner.RunBufferedAsync, versionTimeout ?? CrosswalkClient.DefaultTimeout),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public CrosswalkExecutableResolution Resolve() => _resolution.Value;

    private static CrosswalkExecutableResolution ResolveOnce(string? explicitPath, string? dataRoot,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runVersionAsync, TimeSpan timeout)
    {
        try
        {
            var configured = !string.IsNullOrWhiteSpace(explicitPath);
            if (!configured && string.IsNullOrWhiteSpace(dataRoot))
                return new(CrosswalkDetectOutcome.NotConfigured, null, CrosswalkDiagnosticCategory.NotConfigured)
                { ConfigurationDetail = "No data root or explicit crosswalk executable path was configured; no path was attempted." };
            var path = Path.GetFullPath(configured ? explicitPath! : Path.Combine(dataRoot!, "tools", "crosswalk", "crosswalk.exe"));
            if (!File.Exists(path))
                return configured
                    ? new(CrosswalkDetectOutcome.Missing, null, CrosswalkDiagnosticCategory.MissingBinary)
                        { ConfigurationDetail = $"Crosswalk executable is missing at explicit path '{path}'." }
                    : new(CrosswalkDetectOutcome.NotConfigured, null, CrosswalkDiagnosticCategory.NotConfigured)
                        { ConfigurationDetail = $"Crosswalk executable is missing at '{path}' under orchestrator data root '{Path.GetFullPath(dataRoot!)}'." };

            string hash;
            using (var stream = File.OpenRead(path))
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            var version = "unknown";
            try
            {
                var request = new WorkerProcessRunRequest(
                    $"& '{path.Replace("'", "''")}' --version; exit $LASTEXITCODE", Path.GetDirectoryName(path)!, timeout);
                // RunBufferedAsync owns the bounded subprocess and drains/terminates it on timeout.
                var run = runVersionAsync(request, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
                if (run.ExitCode == 0 && !run.TimedOut && !run.CaptureLimited && !string.IsNullOrWhiteSpace(run.StandardOutput))
                    version = run.StandardOutput.Trim();
            }
            catch (Exception) { /* Version is best effort; path and hash remain valid. */ }
            return new(CrosswalkDetectOutcome.Detected, new(path, version, hash), null);
        }
        catch (Exception exception)
        {
            return new(CrosswalkDetectOutcome.StartFailed, null, CrosswalkDiagnosticCategory.StartFailed, exception.GetType().Name);
        }
    }
}

public sealed record CrosswalkExecutableResolution(string Status, CrosswalkExecutableIdentity? Identity,
    CrosswalkDiagnosticCategory? DiagnosticCategory, string Detail = "")
{
    // Keep legacy Detail text stable; structured check diagnostics carry configuration failures.
    public string? ConfigurationDetail { get; init; }
}
