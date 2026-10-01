using System.ComponentModel;
using System.Diagnostics;

internal sealed record CliChildProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CliChildProcessRunner
{
    internal static readonly TimeSpan DefaultHangGuard = TimeSpan.FromSeconds(120);

    internal static CliChildProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan? hangGuard = null,
        Action<Process>? started = null) =>
        RunAsync(startInfo, hangGuard, started).GetAwaiter().GetResult();

    internal static async Task<CliChildProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan? hangGuard = null,
        Action<Process>? started = null)
    {
        var command = string.Join(" ", new[] { startInfo.FileName }
            .Concat(startInfo.ArgumentList.Select(QuoteArgument)));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start CLI child: {command}");
        if (startInfo.RedirectStandardInput)
            process.StandardInput.Close();

        var outputTask = startInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        var errorTask = startInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        started?.Invoke(process);

        using var guard = new CancellationTokenSource(hangGuard ?? DefaultHangGuard);
        try
        {
            await process.WaitForExitAsync(guard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            var killed = false;
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    killed = true;
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The child exited between the check and the kill.
                }
                catch (Win32Exception ex)
                {
                    throw new TimeoutException($"CLI child {command} did not exit within {hangGuard ?? DefaultHangGuard}; process-tree termination failed: {ex.Message}", ex);
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            var outputAfterKill = await outputTask.ConfigureAwait(false);
            var errorAfterKill = await errorTask.ConfigureAwait(false);
            if (killed)
                throw new TimeoutException($"CLI child {command} did not exit within {hangGuard ?? DefaultHangGuard}. stdout={outputAfterKill} stderr={errorAfterKill}");
            return new CliChildProcessResult(process.ExitCode, outputAfterKill, errorAfterKill);
        }

        return new CliChildProcessResult(process.ExitCode,
            await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
}
