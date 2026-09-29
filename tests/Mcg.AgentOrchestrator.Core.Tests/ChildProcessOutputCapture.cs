using System.Diagnostics;
using System.Text;

internal sealed class ChildProcessOutputCapture
{
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    private readonly object sync = new();
    private readonly Task outputPump;
    private readonly Task errorPump;

    internal ChildProcessOutputCapture(Process process)
    {
        outputPump = Pump(process.StandardOutput, stdout);
        errorPump = Pump(process.StandardError, stderr);
    }

    internal (string Stdout, string Stderr, int StdoutBytes, int StderrBytes) Snapshot()
    {
        lock (sync)
        {
            var output = stdout.ToString();
            var error = stderr.ToString();
            return (output, error, Encoding.UTF8.GetByteCount(output), Encoding.UTF8.GetByteCount(error));
        }
    }

    internal bool JoinAfterExit() => Task.WaitAll([outputPump, errorPump], millisecondsTimeout: 5_000);

    private async Task Pump(StreamReader reader, StringBuilder destination)
    {
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            lock (sync) destination.Append(buffer, 0, read);
        }
    }
}
