using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class CrosswalkClient
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly CrosswalkExecutableResolver _resolver;
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync;
    private readonly TimeSpan _timeout;

    internal CrosswalkClient(
        CrosswalkExecutableResolver resolver,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null,
        TimeSpan? timeout = null)
    {
        _resolver = resolver;
        _runProcessAsync = runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
        _timeout = timeout ?? DefaultTimeout;
    }

    internal CrosswalkDetectOutcome Detect(string root)
    {
        CrosswalkExecutableIdentity? executable = null;
        try
        {
            var resolution = _resolver.Resolve();
            executable = resolution.Identity;
            if (executable is null)
                return new(resolution.Status, null, resolution.Detail);

            var request = new WorkerProcessRunRequest(
                $"& {Quote(executable.Path)} detect {Quote(root)}; exit $LASTEXITCODE", root, _timeout);
            var run = _runProcessAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            if (run.TimedOut)
                return new(CrosswalkDetectOutcome.TimedOut, null, string.Empty) { Executable = executable };
            if (run.ExitCode != 0)
                return new(CrosswalkDetectOutcome.ExitNonZero, null, $"exit={run.ExitCode}") { Executable = executable };
            if (!CrosswalkDetectResult.TryParse(run.StandardOutput, out var result))
                return new(CrosswalkDetectOutcome.InvalidJson, null, string.Empty) { Executable = executable };
            return new(CrosswalkDetectOutcome.Detected, result, string.Empty) { Executable = executable };
        }
        catch (OperationCanceledException)
        {
            return new(CrosswalkDetectOutcome.TimedOut, null, string.Empty) { Executable = executable };
        }
        catch (Exception exception)
        {
            return new(CrosswalkDetectOutcome.StartFailed, null, exception.GetType().Name) { Executable = executable };
        }
    }

    internal CrosswalkListOutcome List(string root)
        => ListAsync(root, _timeout, CancellationToken.None).GetAwaiter().GetResult();

    internal CrosswalkRunOutcome Run(string root, IEnumerable<string> ids, TimeSpan timeout)
        => RunAsync(root, ids, timeout, CancellationToken.None).GetAwaiter().GetResult();

    internal async Task<CrosswalkListOutcome> ListAsync(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var outcome = await ExecuteAsync(root, "list", timeout, json =>
        {
            // Separate syntactically invalid JSON from malformed discovery.
            using var document = JsonDocument.Parse(json);
            return ParseList(json);
        }, cancellationToken).ConfigureAwait(false);
        return new(outcome.Status, outcome.Result, outcome.Detail)
        { ExitCode = outcome.ExitCode, StandardError = outcome.Stderr, Diagnostics = outcome.Diagnostics, Executable = outcome.Executable };
    }

    internal async Task<CrosswalkRunOutcome> RunAsync(string root, IEnumerable<string> ids, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var arguments = string.Concat(ids.Select(id => " --id " + Quote(id)));
        var outcome = await ExecuteAsync<TestResult>(root, "run", timeout, json =>
        {
            using var document = JsonDocument.Parse(json);
            var read = new NormalizedTestResultSource().ReadJson(json);
            return read.Status == TestResultReadStatus.Readable ? read.Records : null;
        }, cancellationToken, arguments).ConfigureAwait(false);
        return new(outcome.Status, outcome.Result, outcome.Detail)
        { ExitCode = outcome.ExitCode, StandardError = outcome.Stderr, Diagnostics = outcome.Diagnostics, Executable = outcome.Executable };
    }

    private async Task<(string Status, IReadOnlyList<T> Result, string Detail, int? ExitCode,
        string Stderr, IReadOnlyList<CrosswalkInvocationDiagnostic> Diagnostics, CrosswalkExecutableIdentity? Executable)> ExecuteAsync<T>(
        string root, string verb, TimeSpan timeout, Func<string, IReadOnlyList<T>?> parse,
        CancellationToken cancellationToken, string arguments = "")
    {
        var diagnostics = new List<CrosswalkInvocationDiagnostic>();
        WorkerProcessRunResult? run = null;
        CrosswalkExecutableIdentity? executable = null;
        string status = CrosswalkDetectOutcome.Detected, detail = "";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = _resolver.Resolve();
            executable = resolution.Identity;
            if (executable is null)
                return Failure(resolution.Status, resolution.DiagnosticCategory!.Value, resolution.Detail);

            var request = new WorkerProcessRunRequest(
                $"& {Quote(executable.Path)} {verb} {Quote(root)}{arguments}; exit $LASTEXITCODE", root, timeout);
            run = await _runProcessAsync(request, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                SetFailure("cancelled", CrosswalkDiagnosticCategory.Cancelled);
            else if (run.TimedOut)
                SetFailure(CrosswalkDetectOutcome.TimedOut, CrosswalkDiagnosticCategory.Timeout);
            else if (run.ExitCode == 2)
                SetFailure("exit-2", CrosswalkDiagnosticCategory.UnsupportedAdapter, run.StandardError);
            else if (run.ExitCode != 0)
                SetFailure(CrosswalkDetectOutcome.ExitNonZero, CrosswalkDiagnosticCategory.NonzeroExit, $"exit={run.ExitCode}");
            if (run.CaptureLimited)
                SetFailure("capture-limited", CrosswalkDiagnosticCategory.CaptureLimited);

            IReadOnlyList<T>? result = null;
            try
            {
                result = parse(run.StandardOutput);
                if (result is null)
                    SetFailure(CrosswalkDetectOutcome.InvalidJson, CrosswalkDiagnosticCategory.MalformedResults);
            }
            catch (JsonException)
            {
                SetFailure(CrosswalkDetectOutcome.InvalidJson, CrosswalkDiagnosticCategory.InvalidJson);
            }
            return (status, result ?? Array.Empty<T>(), detail, run.ExitCode, run.StandardError, diagnostics, executable);
        }
        catch (OperationCanceledException)
        {
            return Failure(cancellationToken.IsCancellationRequested ? "cancelled" : CrosswalkDetectOutcome.TimedOut,
                cancellationToken.IsCancellationRequested ? CrosswalkDiagnosticCategory.Cancelled : CrosswalkDiagnosticCategory.Timeout);
        }
        catch (Exception exception)
        {
            return Failure(CrosswalkDetectOutcome.StartFailed, CrosswalkDiagnosticCategory.StartFailed, exception.GetType().Name);
        }

        void SetFailure(string value, CrosswalkDiagnosticCategory category, string message = "")
        {
            if (diagnostics.Count == 0) { status = value; detail = message; }
            diagnostics.Add(new(category, message));
        }
        (string, IReadOnlyList<T>, string, int?, string, IReadOnlyList<CrosswalkInvocationDiagnostic>, CrosswalkExecutableIdentity?) Failure(
            string value, CrosswalkDiagnosticCategory category, string message = "")
        {
            SetFailure(value, category, message);
            return (status, Array.Empty<T>(), detail, run?.ExitCode, run?.StandardError ?? "", diagnostics, executable);
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
