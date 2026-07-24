using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorSelfRelaunchOptions(
    string RepositoryRoot,
    string AppProjectPath,
    string AppDllPath,
    string AppHeadMarkerPath,
    string UpdateHeadMarkerScriptPath,
    string ResolveRunDirectoryScriptPath,
    string StateStorePath,
    string AgentCatalogPath,
    string WorkerProfilePath,
    string ModelFunctionCatalogPath,
    string DotnetPath,
    string PowerShellPath,
    ConductLoopHandoffOptions HandoffOptions,
    TimeSpan BuildTimeout = default,
    TimeSpan SelfCheckTimeout = default);

internal sealed record ConductorSelfRelaunchRequest(
    string GoalId,
    int Tick);

internal sealed record ConductorPreparedSuccessor(
    string RunDirectory,
    string AppDllPath,
    string GitHead,
    string SelfCheckDetail);

internal sealed record ConductorSelfRelaunchResult(
    bool HandedOff,
    string? FailedPhase,
    string? Reason,
    ConductorLoopHandoffResult? Handoff = null,
    ConductorPreparedSuccessor? Successor = null)
{
    public static ConductorSelfRelaunchResult PreparationFailed(string phase, string reason) =>
        new(false, phase, reason);
}

internal static class ConductorSelfRelaunch
{
    private static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultSelfCheckTimeout = TimeSpan.FromSeconds(30);

    internal static Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult> Create(
        ConductorSelfRelaunchOptions options,
        Func<ConductorSelfRelaunchOptions, ConductorPreparedSuccessor>? prepare = null,
        Func<ConductLoopHandoffOptions, ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? handoff = null) =>
        request => TryRelaunch(
            options,
            request,
            prepare ?? PrepareSuccessor,
            handoff ?? ((handoffOptions, handoffRequest) =>
                ConductorLoopHandoff.Create(handoffOptions)(handoffRequest)));

    internal static ConductorSelfRelaunchResult TryRelaunch(
        ConductorSelfRelaunchOptions options,
        ConductorSelfRelaunchRequest request,
        Func<ConductorSelfRelaunchOptions, ConductorPreparedSuccessor> prepare,
        Func<ConductLoopHandoffOptions, ConductorLoopHandoffRequest, ConductorLoopHandoffResult> handoff)
    {
        ConductorPreparedSuccessor successor;
        try
        {
            successor = prepare(options);
        }
        catch (ConductorSelfRelaunchPreparationException ex)
        {
            return ConductorSelfRelaunchResult.PreparationFailed(ex.Phase, ex.Message);
        }
        catch (Exception ex)
        {
            return ConductorSelfRelaunchResult.PreparationFailed(
                "build",
                $"{ex.GetType().Name}: {ex.Message}");
        }

        var commandPrefix = new[] { options.DotnetPath, successor.AppDllPath };
        var handoffOptions = options.HandoffOptions with { SuccessorCommandPrefix = commandPrefix };
        ConductorLoopHandoffResult handoffResult;
        try
        {
            handoffResult = handoff(
                handoffOptions,
                new ConductorLoopHandoffRequest(
                    request.Tick,
                    TimeSpan.Zero,
                    Done: 0,
                    LandedGoalDelta: 1));
        }
        catch (Exception ex)
        {
            try
            {
                handoffOptions.ReacquireCurrentLease?.Invoke();
            }
            catch (Exception reacquireException)
            {
                throw new InvalidOperationException(
                    "Self-relaunch handoff failed and the incumbent could not reacquire its authority lease.",
                    new AggregateException(ex, reacquireException));
            }

            return new ConductorSelfRelaunchResult(
                false,
                "handoff",
                $"{ex.GetType().Name}: {ex.Message}",
                Successor: successor);
        }
        return handoffResult.Started
            ? new ConductorSelfRelaunchResult(true, null, null, handoffResult, successor)
            : new ConductorSelfRelaunchResult(
                false,
                "handoff",
                handoffResult.Reason ?? "successor handoff did not start",
                handoffResult,
                successor);
    }

    private static ConductorPreparedSuccessor PrepareSuccessor(ConductorSelfRelaunchOptions options)
    {
        var buildTimeout = options.BuildTimeout <= TimeSpan.Zero
            ? DefaultBuildTimeout
            : options.BuildTimeout;
        var selfCheckTimeout = options.SelfCheckTimeout <= TimeSpan.Zero
            ? DefaultSelfCheckTimeout
            : options.SelfCheckTimeout;

        var gitHeadResult = RunProcess(
            "git",
            ["-C", options.RepositoryRoot, "rev-parse", "HEAD"],
            options.RepositoryRoot,
            TimeSpan.FromSeconds(30));
        EnsureSucceeded("build", "resolve merged git HEAD", gitHeadResult);
        var gitHead = gitHeadResult.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(gitHead))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "build",
                "Merged git HEAD was empty.");
        }

        var build = RunProcess(
            options.DotnetPath,
            [
                "build",
                options.AppProjectPath,
                "--nologo",
                "-v",
                "quiet",
                "-clp:ErrorsOnly"
            ],
            options.RepositoryRoot,
            buildTimeout);
        EnsureSucceeded("build", "build merged conductor", build);

        var marker = RunPowerShell(
            options,
            options.UpdateHeadMarkerScriptPath,
            [options.RepositoryRoot, options.AppHeadMarkerPath],
            TimeSpan.FromMinutes(1));
        EnsureSucceeded("build", "record conductor git HEAD", marker);

        var resolve = RunPowerShell(
            options,
            options.ResolveRunDirectoryScriptPath,
            [options.AppDllPath],
            TimeSpan.FromMinutes(2));
        EnsureSucceeded("build", "publish content-addressed run directory", resolve);
        var runDirectory = resolve.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "build",
                "Run-directory resolver returned no content-addressed directory.");
        }

        var successorDll = Path.Combine(runDirectory, Path.GetFileName(options.AppDllPath));
        var selfCheck = RunProcess(
            options.DotnetPath,
            [
                successorDll,
                ConductorSuccessorSelfCheck.SubcommandName,
                options.RepositoryRoot,
                options.StateStorePath,
                gitHead,
                options.AgentCatalogPath,
                options.WorkerProfilePath,
                options.ModelFunctionCatalogPath
            ],
            options.RepositoryRoot,
            selfCheckTimeout);
        EnsureSucceeded("self-check", "probe successor startup contract", selfCheck);
        var readiness = selfCheck.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("LOOP_START ", StringComparison.Ordinal));
        if (readiness is null ||
            !readiness.Contains($"gitHead={gitHead}", StringComparison.OrdinalIgnoreCase) ||
            !readiness.Contains($"runDir={Path.TrimEndingDirectorySeparator(runDirectory)}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "self-check",
                "Successor did not return the compatible LOOP_START readiness contract.");
        }

        return new ConductorPreparedSuccessor(runDirectory, successorDll, gitHead, readiness);
    }

    private static CapturedProcessResult RunPowerShell(
        ConductorSelfRelaunchOptions options,
        string scriptPath,
        IReadOnlyList<string> scriptArguments,
        TimeSpan timeout)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };
        arguments.AddRange(scriptArguments);
        return RunProcess(options.PowerShellPath, arguments, options.RepositoryRoot, timeout);
    }

    private static CapturedProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
            }

            return new CapturedProcessResult(
                -1,
                stdoutTask.GetAwaiter().GetResult(),
                stderrTask.GetAwaiter().GetResult(),
                TimedOut: true);
        }

        return new CapturedProcessResult(
            process.ExitCode,
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }

    private static void EnsureSucceeded(
        string phase,
        string operation,
        CapturedProcessResult result)
    {
        if (result.ExitCode == 0 && !result.TimedOut)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout
            : result.Stderr;
        detail = Sanitize(detail);
        if (detail.Length > 1000)
        {
            detail = detail[^1000..];
        }

        throw new ConductorSelfRelaunchPreparationException(
            phase,
            $"{operation} failed exit={result.ExitCode} timedOut={result.TimedOut}: {detail}");
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private sealed record CapturedProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut = false);
}

internal sealed class ConductorSelfRelaunchPreparationException(string phase, string message)
    : InvalidOperationException(message)
{
    internal string Phase { get; } = phase;
}
