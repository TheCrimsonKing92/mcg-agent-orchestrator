using System.Diagnostics;
using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorDiagnosticDumpCapture
{
    Task<ConductorDumpCaptureResult> CaptureAsync(
        int? processId, string outputDirectory, CancellationToken cancellationToken);
}

internal sealed record ConductorDumpCaptureResult(bool Captured, string? DumpPath, string Reason)
{
    internal static ConductorDumpCaptureResult NotCaptured(string reason) => new(false, null, reason);
}

internal sealed class DotnetDumpConductorDiagnosticDumpCapture(
    Func<string?>? resolveTool = null,
    Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<int>>? runProcess = null,
    TimeSpan? timeout = null) : IConductorDiagnosticDumpCapture
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);

    public async Task<ConductorDumpCaptureResult> CaptureAsync(
        int? processId, string outputDirectory, CancellationToken cancellationToken)
    {
        if (processId is not > 0) return ConductorDumpCaptureResult.NotCaptured("pid-unknown");
        var tool = resolveTool is null ? ResolveTool() : resolveTool();
        if (string.IsNullOrWhiteSpace(tool)) return ConductorDumpCaptureResult.NotCaptured("tool-missing");

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory,
            $"conductor-tick-stall-{processId}-{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}.dmp");
        var args = new[] { "collect", "-p", processId.Value.ToString(CultureInfo.InvariantCulture),
            "-o", path, "--type", "Heap" };
        try
        {
            var exitCode = await (runProcess ?? RunProcessAsync)(tool, args, _timeout, cancellationToken)
                .ConfigureAwait(false);
            return exitCode == 0 && File.Exists(path)
                ? new ConductorDumpCaptureResult(true, path, "captured")
                : ConductorDumpCaptureResult.NotCaptured(exitCode == 0 ? "output-missing" : $"exit={exitCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ConductorDumpCaptureResult.NotCaptured("timeout");
        }
        catch (Exception ex)
        {
            return ConductorDumpCaptureResult.NotCaptured($"error={ex.GetType().Name}");
        }
    }

    private static string? ResolveTool()
    {
        var configured = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DUMP_TOOL");
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, "dotnet-dump" + extension.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static async Task<int> RunProcessAsync(
        string tool, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }
}
