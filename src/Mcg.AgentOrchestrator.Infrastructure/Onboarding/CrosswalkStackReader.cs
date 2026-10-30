using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads crosswalk discovery with the gate's executable resolver and discovery budget.</summary>
public sealed class CrosswalkStackReader
{
    private readonly CrosswalkExecutableResolver _resolver;
    private readonly TimeSpan _timeout;
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync;

    public CrosswalkStackReader(CrosswalkExecutableResolver resolver, TimeSpan timeout,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null)
    {
        _resolver = resolver;
        _timeout = timeout;
        _runProcessAsync = runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
    }

    internal CrosswalkStackReport Detect(string repositoryRoot)
    {
        var resolution = _resolver.Resolve();
        if (resolution.Identity is not { } executable)
            throw new InvalidOperationException($"Crosswalk onboarding detection failed: {resolution.ConfigurationDetail ?? resolution.Detail} ({resolution.Status}).");
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            var run = _runProcessAsync(new(CrosswalkClient.Command(executable.Path, "detect", root), root, _timeout),
                CancellationToken.None).GetAwaiter().GetResult();
            if (run.TimedOut)
                throw new InvalidOperationException("detect timed out");
            if (run.ExitCode != 0)
                throw new InvalidOperationException($"detect exit={run.ExitCode}");
            if (run.CaptureLimited)
                throw new InvalidOperationException("detect output capture was limited");
            using var document = JsonDocument.Parse(run.StandardOutput);
            var report = document.RootElement;
            if (report.ValueKind != JsonValueKind.Object ||
                !report.TryGetProperty("stack", out var stack) || stack.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(stack.GetString()) ||
                !report.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
                throw new JsonException("Expected stack and evidence from crosswalk detect.");
            foreach (var item in evidence.EnumerateArray())
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String)
                    throw new JsonException("Expected path and reason in crosswalk evidence.");
            string? framework = null;
            if (report.TryGetProperty("testFramework", out var value))
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new JsonException("Expected a string testFramework from crosswalk detect.");
                framework = value.GetString();
            }
            return new(stack.GetString()!, framework, evidence.GetRawText());
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Crosswalk onboarding detection failed at '{executable.Path}': {exception.Message}", exception);
        }
    }

    internal CrosswalkListOutcome List(string repositoryRoot) =>
        new CrosswalkClient(_resolver, _runProcessAsync).ListAsync(Path.GetFullPath(repositoryRoot), _timeout,
            CancellationToken.None).GetAwaiter().GetResult();
}
