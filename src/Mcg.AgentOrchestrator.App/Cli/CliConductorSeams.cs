using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ConductorLaunchRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyCollection<string> EnvironmentVariablesToRemove);

internal sealed record ConductorLaunchResult(int ExitCode, string? StandardOutput, string? StandardError);

internal interface IConductorProcessLauncher
{
    ConductorLaunchResult Launch(ConductorLaunchRequest request);
}

internal sealed class SystemConductorProcessLauncher : IConductorProcessLauncher
{
    public ConductorLaunchResult Launch(ConductorLaunchRequest request)
    {
        var start = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in request.Environment) start.Environment[key] = value;
        foreach (var key in request.EnvironmentVariablesToRemove) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch PowerShell.");
        process.StandardInput.Close();
        // The detached child can inherit a pipe handle. Bound reads by the launcher process,
        // never by EOF from its descendants.
        var outputLine = process.StandardOutput.ReadLineAsync();
        var errorLine = process.StandardError.ReadLineAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: false);
            throw new TimeoutException("Conductor launcher did not return within 30 seconds.");
        }
        var output = outputLine.Wait(TimeSpan.FromSeconds(2)) ? outputLine.Result : null;
        var error = errorLine.Wait(TimeSpan.FromSeconds(2)) ? errorLine.Result : null;
        return new ConductorLaunchResult(process.ExitCode, output, error);
    }
}

internal interface IConductorLockProbe
{
    int? ActiveOwnerPid(OrchestratorWorkspace workspace);
}

internal sealed class SystemConductorLockProbe : IConductorLockProbe
{
    public int? ActiveOwnerPid(OrchestratorWorkspace workspace)
    {
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory)) return null;
        var path = Path.Combine(workspace.OrchestratorDirectory, "conduct-loop.lock");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var firstLine = reader.ReadLine();
        if (!int.TryParse(firstLine, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            throw new InvalidOperationException($"Invalid conductor lock: {path}");
        return pid;
    }
}
