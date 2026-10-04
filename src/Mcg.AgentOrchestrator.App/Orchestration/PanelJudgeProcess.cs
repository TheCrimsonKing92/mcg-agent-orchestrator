using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PanelProcessResult(int? ExitCode, string Stdout, string Stderr,
    bool TimedOut = false, string? Failure = null);

// Unlike the generic worker runner, retain partial raw output when a judge times out.
internal static class PanelJudgeProcess
{
    internal static async Task<PanelProcessResult> RunAsync(WorkerProcessRunRequest request, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(request.Timeout ?? ConductorJudgePanelBudgets.JudgeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        using var process = new Process { StartInfo = WorkerProcessRunner.BuildPowerShellStartInfo(request.Command, request.WorkingDirectory) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Task? drains = null;
        bool timedOut = false;
        string? failure = null;
        int? exitCode = null;
        using var drainStop = new CancellationTokenSource();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Judge process did not start.");
            drains = Task.WhenAll(Capture(process.StandardOutput, stdout, drainStop.Token),
                Capture(process.StandardError, stderr, drainStop.Token));
            await process.StandardInput.WriteAsync((request.StandardInput ?? "").AsMemory(), linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException) { timedOut = true; }
        catch (Exception exception) { failure = exception.Message; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (drains is not null)
            {
                drainStop.CancelAfter(TimeSpan.FromSeconds(5));
                try { await drains.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
            try { if (process.HasExited) exitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
        }
        return new(exitCode, stdout.ToString(), stderr.ToString(), timedOut, failure);
    }

    private static async Task Capture(StreamReader reader, StringBuilder buffer, CancellationToken token)
    {
        var chunk = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) != 0)
            buffer.Append(chunk, 0, count);
    }
}
