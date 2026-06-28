using System.Diagnostics;
using System.Runtime.CompilerServices;

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

        try { process.StandardInput.Close(); } catch { }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        try
        {
            return new WorkerProcessRunResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
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
            TryKillProcessTree(process);
            throw;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
        try { process.WaitForExit(5000); } catch { }
    }
}
