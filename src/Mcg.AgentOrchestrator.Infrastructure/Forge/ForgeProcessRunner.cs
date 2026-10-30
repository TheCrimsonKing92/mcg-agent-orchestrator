using System.ComponentModel;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Runs read-only forge queries directly with redirected input and a bounded lifetime.</summary>
public sealed class ForgeProcessRunner : IForgeProcessRunner
{
    public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(false, -1, "", $"{fileName} did not start");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            return new(false, -1, "", exception.Message);
        }

        process.StandardInput.Close();
        using var capture = new CancellationTokenSource();
        var stdout = process.StandardOutput.ReadToEndAsync(capture.Token);
        var stderr = process.StandardError.ReadToEndAsync(capture.Token);
        var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        // A descendant retaining an output pipe must not keep discovery waiting indefinitely.
        capture.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            return new(true, exited ? process.ExitCode : -1, stdout.Result, stderr.Result, !exited);
        }
        catch (OperationCanceledException)
        {
            return new(true, -1, "", "forge output capture timed out", true);
        }
    }
}
