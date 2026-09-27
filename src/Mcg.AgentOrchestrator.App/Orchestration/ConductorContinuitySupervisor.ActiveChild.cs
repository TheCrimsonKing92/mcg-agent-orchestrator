using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    private async Task<TickStallOutcome> ObserveInheritedChildAsync(
        ConductorSupervisorActiveChild child, ConductorActivationBuild build,
        string outputDirectory, CancellationToken cancellationToken)
    {
        var monitor = new ActivationMonitor(_timeProvider.GetUtcNow);
        using var processCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var childTask = supervisorHandoff!.Seam.ObserveChildAsync(child, processCts.Token);
        var tailTask = TailInheritedOutputAsync(child.StdoutPath, monitor, tailCts.Token);
        try
        {
            return await WatchTickProgressAsync(childTask, monitor,
                () => child.Process.ProcessId, processCts, child.Attempt, build,
                outputDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tailCts.Cancel();
            try { await tailTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (tailCts.IsCancellationRequested) { }
        }
    }

    private async Task TailInheritedOutputAsync(string path, ActivationMonitor monitor,
        CancellationToken cancellationToken)
    {
        long offset = 0;
        var partialLine = string.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length < offset)
                    {
                        offset = 0;
                        partialLine = string.Empty;
                    }
                    stream.Position = offset;
                    using var reader = new StreamReader(stream);
                    var text = partialLine + await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    offset = stream.Position;
                    var lines = text.Split('\n');
                    for (var index = 0; index < lines.Length - 1; index++)
                        monitor.OnLine(lines[index].TrimEnd('\r'));
                    partialLine = lines[^1];
                }
            }
            catch (IOException)
            {
                // The incumbent is still writing this log; retry on the next poll.
            }
            await _delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }
}
