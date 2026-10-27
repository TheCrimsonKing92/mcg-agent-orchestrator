namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class CrosswalkClient
{
    internal const string PathVariable = "MCG_ORCHESTRATOR_CROSSWALK_PATH";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, string?> _readEnvironment;
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync;
    private readonly TimeSpan _timeout;

    internal CrosswalkClient(
        Func<string, string?>? readEnvironment = null,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null,
        TimeSpan? timeout = null)
    {
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        _runProcessAsync = runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
        _timeout = timeout ?? DefaultTimeout;
    }

    internal CrosswalkDetectOutcome Detect(string root)
    {
        try
        {
            var path = _readEnvironment(PathVariable);
            if (string.IsNullOrWhiteSpace(path))
                return new(CrosswalkDetectOutcome.NotConfigured, null, string.Empty);
            if (!File.Exists(path))
                return new(CrosswalkDetectOutcome.Missing, null, string.Empty);

            var request = new WorkerProcessRunRequest(
                $"& {Quote(path)} detect {Quote(root)}; exit $LASTEXITCODE", root, _timeout);
            var run = _runProcessAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            if (run.ExitCode != 0)
                return new(CrosswalkDetectOutcome.ExitNonZero, null, $"exit={run.ExitCode}");
            if (!CrosswalkDetectResult.TryParse(run.StandardOutput, out var result))
                return new(CrosswalkDetectOutcome.InvalidJson, null, string.Empty);
            return new(CrosswalkDetectOutcome.Detected, result, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new(CrosswalkDetectOutcome.TimedOut, null, string.Empty);
        }
        catch (Exception exception)
        {
            return new(CrosswalkDetectOutcome.StartFailed, null, exception.GetType().Name);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
