using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class HandleListConductorProcessLauncher : IConductorProcessLauncher
{
    public ConductorLaunchResult Launch(ConductorLaunchRequest request)
    {
        var command = ConductorLoopHandoff.ResolveWindowsNativeCommand(
            [request.FileName, .. request.Arguments], request.WorkingDirectory);
        var commandLine = ConductorLoopHandoff.BuildWindowsProcessCommandLine(command);
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "",
                StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Environment) environment[key] = value;
        foreach (var key in request.EnvironmentVariablesToRemove) environment.Remove(key);
        var environmentBlock = WindowsHandleListProcessLauncher.BuildEnvironmentBlock(environment);
        var stdout = IntPtr.Zero;
        var stderr = IntPtr.Zero;
        var created = false;
        try
        {
            stdout = WindowsHandleListProcessLauncher.CreateInheritedOutputFile(request.StdoutPath!);
            stderr = WindowsHandleListProcessLauncher.CreateInheritedOutputFile(request.StderrPath!);
            using var suppression = ProcessTreeGuiSuppression.AcquireConsoleForChildSpawn();
            // Start must remain detached even when the operator has an attached console.
            // Self-relaunch keeps its existing console policy; only this entry point forces isolation.
            var flags = ConductorLoopHandoff.WindowsSuccessorCreationFlags |
                (uint)WindowsHandleListProcessLauncher.WindowsCreationFlags.CreateNoWindow;
            var startedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var outcome = WindowsHandleListProcessLauncher.Launch(command[0], commandLine,
                environmentBlock, request.WorkingDirectory, flags, stdout, stderr);
            if (!outcome.Created && DetachedLaunchBreakawayFallback.TryGetRetryFlags(
                    flags, outcome.NativeError, out var retryFlags))
                outcome = WindowsHandleListProcessLauncher.Launch(command[0], commandLine,
                    environmentBlock, request.WorkingDirectory, retryFlags, stdout, stderr);
            if (!outcome.Created) throw new Win32Exception(outcome.NativeError);
            created = true;
            try
            {
                return new ConductorLaunchResult(0, JsonSerializer.Serialize(new
                {
                    pid = outcome.ProcessId,
                    stdoutPath = request.StdoutPath,
                    stderrPath = request.StderrPath,
                    args = request.Arguments,
                    startedAt
                }), null);
            }
            finally
            {
                WindowsHandleListProcessLauncher.CloseHandle(outcome.ThreadHandle);
                WindowsHandleListProcessLauncher.CloseHandle(outcome.ProcessHandle);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new ConductorLaunchResult(1, null, JsonSerializer.Serialize(new
            {
                reason = ex.Message,
                stdoutPath = request.StdoutPath,
                stderrPath = request.StderrPath,
                args = request.Arguments
            }));
        }
        finally
        {
            if (stdout != IntPtr.Zero) WindowsHandleListProcessLauncher.CloseHandle(stdout);
            if (stderr != IntPtr.Zero) WindowsHandleListProcessLauncher.CloseHandle(stderr);
            if (!created)
            {
                if (stdout != IntPtr.Zero) DeleteFailedOutput(request.StdoutPath!);
                if (stderr != IntPtr.Zero) DeleteFailedOutput(request.StderrPath!);
            }
        }
    }

    private static void DeleteFailedOutput(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
