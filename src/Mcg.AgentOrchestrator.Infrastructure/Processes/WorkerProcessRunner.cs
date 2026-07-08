using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProcessRunRequest(
    string Command,
    string WorkingDirectory,
    TimeSpan? Timeout = null);

public sealed record WorkerProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public static class WorkerProcessRunner
{
    public static ProcessStartInfo BuildPowerShellStartInfo(
        string command,
        string workingDirectory,
        bool redirectStandardInput = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        foreach (var argument in WorkerShell.BaseArguments())
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    public static async Task<WorkerProcessRunResult> RunBufferedAsync(
        WorkerProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = request.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();
        using var linkedCts = request.Timeout is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var startInfo = BuildPowerShellStartInfo(request.Command, request.WorkingDirectory);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start worker process.");
        OwnedProcessGroup? processGroup = null;
        try
        {
            processGroup = OwnedProcessGroup.Attach(process);
        }
        catch
        {
            // Process groups are a cleanup backstop. If assignment is unavailable, keep the
            // command path alive and fall back to direct tree-kill on cancellation.
        }

        try
        {
            try { process.StandardInput.Close(); } catch { }

            using var stdout = new MemoryStream();
            using var stderr = new MemoryStream();
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, drainCts.Token);
            var stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr, drainCts.Token);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            await DrainOutputAsync(process, processGroup, drainCts, stdoutTask, stderrTask).ConfigureAwait(false);

            return new WorkerProcessRunResult(
                process.ExitCode,
                Encoding.UTF8.GetString(stdout.ToArray()),
                Encoding.UTF8.GetString(stderr.ToArray()));
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process, processGroup);
            throw;
        }
        finally
        {
            WorkerProcessJobs.ReadAccountingAndDispose(processGroup, kill: false, out _);
        }
    }

    public static async IAsyncEnumerable<string> RunStreamingAsync(
        WorkerProcessRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startInfo = BuildPowerShellStartInfo(request.Command, request.WorkingDirectory);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start worker process.");

        try { process.StandardInput.Close(); } catch { }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            yield return line;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process, null);
            throw;
        }
    }

    private static async Task DrainOutputAsync(
        Process process,
        OwnedProcessGroup? processGroup,
        CancellationTokenSource drainCts,
        Task stdoutTask,
        Task stderrTask)
    {
        const int DrainTimeoutMs = 12_000;
        var drainTask = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(drainTask, Task.Delay(DrainTimeoutMs)).ConfigureAwait(false) == drainTask)
        {
            await drainTask.ConfigureAwait(false);
            return;
        }

        drainCts.Cancel();
        TryKillProcessTree(process, processGroup);
        try { await Task.WhenAny(drainTask, Task.Delay(2000)).ConfigureAwait(false); } catch { }
    }

    private static void TryKillProcessTree(Process process, OwnedProcessGroup? processGroup)
    {
        try { WorkerProcessJobs.ReadAccountingAndDispose(processGroup, kill: true, out _); } catch { }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { process.WaitForExit(5000); } catch { }
    }
}
