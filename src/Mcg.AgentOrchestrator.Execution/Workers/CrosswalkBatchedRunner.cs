namespace Mcg.AgentOrchestrator.Infrastructure;

// Coordinates an explicit selection; CrosswalkClient still executes exactly one run per call.
internal sealed class CrosswalkBatchedRunner(CrosswalkClient client, int commandLineLimit)
{
    internal async Task<CrosswalkRunOutcome> RunAsync(string root, CrosswalkExecutableIdentity executable,
        IReadOnlyList<string> ids, Func<TimeSpan> remaining, CancellationToken cancellationToken)
    {
        if (commandLineLimit <= 0) throw new ArgumentOutOfRangeException(nameof(commandLineLimit));
        if (ids.Count == 0) throw new ArgumentException("Batching requires an explicit selection.", nameof(ids));

        var batches = new List<string[]>();
        var batch = new List<string>();
        var baseLength = CrosswalkClient.Command(executable.Path, "run", root).Length;
        var length = baseLength;
        for (var index = 0; index < ids.Count; index++)
        {
            var argumentLength = CrosswalkClient.IdArgument(ids[index]).Length;
            if ((long)baseLength + argumentLength >= commandLineLimit)
                return new(CrosswalkDetectOutcome.StartFailed, [], "Selected ID cannot fit in a command batch.")
                {
                    Executable = executable,
                    Diagnostics = [new(CrosswalkDiagnosticCategory.StartFailed,
                        $"batch {batches.Count + 1}: selected ID at position {index + 1} exceeds the command-line limit {commandLineLimit}.")]
                };
            if ((long)length + argumentLength >= commandLineLimit)
            {
                batches.Add(batch.ToArray());
                batch.Clear();
                length = baseLength;
            }
            batch.Add(ids[index]);
            length += argumentLength;
        }
        batches.Add(batch.ToArray());

        var results = new List<TestResult>();
        var diagnostics = new List<CrosswalkInvocationDiagnostic>();
        var stderr = new System.Text.StringBuilder();
        CrosswalkRunOutcome? firstFailure = null;
        int? exitCode = 0;
        for (var index = 0; index < batches.Count; index++)
        {
            var budget = remaining();
            if (cancellationToken.IsCancellationRequested || budget <= TimeSpan.Zero)
            {
                var cancelled = cancellationToken.IsCancellationRequested;
                var detail = $"batch {index + 1}/{batches.Count}: shared check deadline or cancellation reached before launch.";
                diagnostics.Add(new(cancelled ? CrosswalkDiagnosticCategory.Cancelled : CrosswalkDiagnosticCategory.Timeout, detail));
                return Merge(cancelled ? "cancelled" : CrosswalkDetectOutcome.TimedOut, detail, null);
            }

            var run = await client.RunAsync(root, batches[index], budget, cancellationToken).ConfigureAwait(false);
            results.AddRange(run.Result);
            stderr.Append(run.StandardError);
            diagnostics.AddRange(run.Diagnostics.Select(diagnostic => diagnostic.Category is
                CrosswalkDiagnosticCategory.StartFailed or CrosswalkDiagnosticCategory.Timeout or CrosswalkDiagnosticCategory.Cancelled
                    ? diagnostic with { Detail = $"batch {index + 1}/{batches.Count}: {diagnostic.Detail}" }
                    : diagnostic));
            if (run.Diagnostics.Count > 0) firstFailure ??= run;
            if (exitCode == 0) exitCode = run.ExitCode;

            // NonzeroExit can describe ordinary test failures: collect the remaining batches.
            if (run.ExitCode is null || run.Diagnostics.Any(diagnostic => diagnostic.Category != CrosswalkDiagnosticCategory.NonzeroExit))
                return Merge(firstFailure?.Status ?? run.Status, firstFailure?.Detail ?? run.Detail,
                    index == batches.Count - 1 && run.ExitCode is not null ? exitCode : null);
        }
        return Merge(firstFailure?.Status ?? CrosswalkDetectOutcome.Detected, firstFailure?.Detail ?? "", exitCode);

        CrosswalkRunOutcome Merge(string status, string detail, int? code) => new(status, results, detail)
        {
            Executable = executable, ExitCode = code, StandardError = stderr.ToString(), Diagnostics = diagnostics
        };
    }
}
