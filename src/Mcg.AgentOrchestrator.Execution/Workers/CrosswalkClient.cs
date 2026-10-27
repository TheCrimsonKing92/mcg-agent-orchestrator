using System.Text.Json;

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

    internal CrosswalkListOutcome List(string root)
    {
        var outcome = Execute(root, "list", _timeout, ParseList);
        return new(outcome.Status, outcome.Result, outcome.Detail);
    }

    internal CrosswalkRunOutcome Run(string root, IEnumerable<string> ids, TimeSpan timeout)
    {
        var arguments = string.Concat(ids.Select(id => " --id " + Quote(id)));
        var outcome = Execute<TestResult>(root, "run", timeout, json =>
        {
            var read = new NormalizedTestResultSource().ReadJson(json);
            return read.Status == TestResultReadStatus.Readable ? read.Records : null;
        }, arguments);
        return new(outcome.Status, outcome.Result, outcome.Detail);
    }

    private (string Status, IReadOnlyList<T> Result, string Detail) Execute<T>(
        string root, string verb, TimeSpan timeout, Func<string, IReadOnlyList<T>?> parse, string arguments = "")
    {
        try
        {
            var path = _readEnvironment(PathVariable);
            if (string.IsNullOrWhiteSpace(path))
                return (CrosswalkDetectOutcome.NotConfigured, Array.Empty<T>(), string.Empty);
            if (!File.Exists(path))
                return (CrosswalkDetectOutcome.Missing, Array.Empty<T>(), string.Empty);

            var request = new WorkerProcessRunRequest(
                $"& {Quote(path)} {verb} {Quote(root)}{arguments}; exit $LASTEXITCODE", root, timeout);
            var run = _runProcessAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            if (run.ExitCode == 2)
                return ("exit-2", Array.Empty<T>(), run.StandardError);
            if (run.ExitCode != 0)
                return (CrosswalkDetectOutcome.ExitNonZero, Array.Empty<T>(), $"exit={run.ExitCode}");
            if (parse(run.StandardOutput) is not { } result)
                return (CrosswalkDetectOutcome.InvalidJson, Array.Empty<T>(), string.Empty);
            return (CrosswalkDetectOutcome.Detected, result, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (CrosswalkDetectOutcome.TimedOut, Array.Empty<T>(), string.Empty);
        }
        catch (Exception exception)
        {
            return (CrosswalkDetectOutcome.StartFailed, Array.Empty<T>(), exception.GetType().Name);
        }
    }

    private static IReadOnlyList<CrosswalkTestRef>? ParseList(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var refs = new List<CrosswalkTestRef>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String)
                    return null;
                refs.Add(new(id.GetString()!, name.GetString()!, file.GetString()!));
            }
            return refs;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
