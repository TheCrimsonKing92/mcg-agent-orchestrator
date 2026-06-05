using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundDispatchRunner
{
    public TaskProcessRecord StartLatestDispatch(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string logRoot)
    {
        var task = kernel.GetTask(goalId, taskId);
        var dispatch = task.LastDispatch
            ?? throw new InvalidOperationException($"Task '{taskId}' has no dispatch to start.");

        Directory.CreateDirectory(logRoot);
        var prefix = $"{goalId.Value[..8]}-{taskId.Value[..8]}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var stdoutPath = Path.Combine(logRoot, $"{prefix}.out.log");
        var stderrPath = Path.Combine(logRoot, $"{prefix}.err.log");
        var exitCodePath = Path.Combine(logRoot, $"{prefix}.exit.txt");

        var wrapper = BuildWrapper(dispatch.Command, stdoutPath, stderrPath, exitCodePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dispatch.WorkingDirectory
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.CreateNewProcessGroup = true;
        }

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(wrapper);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start background dispatch process.");

        var record = new TaskProcessRecord(
            process.Id,
            dispatch.Command,
            dispatch.WorkingDirectory,
            stdoutPath,
            stderrPath,
            exitCodePath,
            DateTimeOffset.UtcNow,
            null,
            null);

        kernel.RecordTaskProcessStarted(goalId, taskId, record);
        return record;
    }

    public TaskProcessRecord RefreshLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to refresh.");

        if (IsStillRunning(processRecord.ProcessId))
        {
            kernel.RecordTaskProcessRefreshed(goalId, taskId, processRecord, null);
            return processRecord;
        }

        var exitCode = ReadExitCode(processRecord.ExitCodePath);
        var completed = processRecord with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            ExitCode = exitCode
        };

        var verification = new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            exitCode,
            ReadIfExists(processRecord.StandardOutputPath),
            ReadIfExists(processRecord.StandardErrorPath),
            completed.CompletedAt.Value);

        kernel.RecordTaskProcessRefreshed(goalId, taskId, completed, verification);
        return completed;
    }

    public TaskProcessRecord CancelLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to cancel.");

        if (processRecord.IsRunning)
        {
            try
            {
                var process = Process.GetProcessById(processRecord.ProcessId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                // Process already exited; still record the user-requested cancellation.
            }
        }

        var cancelled = processRecord with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = true
        };

        kernel.RecordTaskProcessCancelled(goalId, taskId, cancelled);
        return cancelled;
    }

    private static bool IsStillRunning(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int ReadExitCode(string path)
    {
        if (!File.Exists(path))
        {
            return 1;
        }

        return int.TryParse(File.ReadAllText(path).Trim(), out var exitCode) ? exitCode : 1;
    }

    private static string ReadIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string BuildWrapper(string command, string stdoutPath, string stderrPath, string exitCodePath)
    {
        return
            $"& {{ {command} }} 1> {Quote(stdoutPath)} 2> {Quote(stderrPath)}; " +
            "$code = if ($global:LASTEXITCODE -ne $null) { $global:LASTEXITCODE } elseif ($?) { 0 } else { 1 }; " +
            $"[IO.File]::WriteAllText({Quote(exitCodePath)}, [string]$code); exit $code";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
