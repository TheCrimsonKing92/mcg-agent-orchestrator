using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial record ConductorSelfRelaunchOptions(
    string RepositoryRoot,
    string AppProjectPath,
    string AppDllPath,
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
    TimeSpan SelfCheckTimeout = default)
{
    internal ConductorSuccessorStagingOptions Staging => new(
        RepositoryRoot,
        AppProjectPath,
        AppDllPath,
        UpdateHeadMarkerScriptPath,
        ResolveRunDirectoryScriptPath,
        StateStorePath,
        AgentCatalogPath,
        WorkerProfilePath,
        ModelFunctionCatalogPath,
        DotnetPath,
        PowerShellPath,
        BuildTimeout,
        SelfCheckTimeout)
    {
        PrebuiltAppOutputDirectory = PrebuiltAppOutputDirectory,
        LandingAppBuildStore = LandingAppBuildStore
    };
}

internal sealed partial record ConductorSuccessorStagingOptions(
    string RepositoryRoot,
    string AppProjectPath,
    string AppDllPath,
    string UpdateHeadMarkerScriptPath,
    string ResolveRunDirectoryScriptPath,
    string StateStorePath,
    string AgentCatalogPath,
    string WorkerProfilePath,
    string ModelFunctionCatalogPath,
    string DotnetPath,
    string PowerShellPath,
    TimeSpan BuildTimeout = default,
    TimeSpan SelfCheckTimeout = default);

internal sealed record ConductorSelfRelaunchRequest(
    string GoalId,
    int Tick);

internal sealed record ConductorPreparedSuccessor(
    string RunDirectory,
    string AppDllPath,
    string RepositoryHead,
    string StagedSourceCommit,
    string SelfCheckDetail,
    IDisposable? RunDirectoryLease = null);

internal sealed record ConductorSelfRelaunchResult(
    bool HandedOff,
    string? FailedPhase,
    string? Reason,
    ConductorLoopHandoffResult? Handoff = null,
    ConductorPreparedSuccessor? Successor = null,
    bool IncumbentCanContinue = true)
{
    public static ConductorSelfRelaunchResult PreparationFailed(string phase, string reason) =>
        new(false, phase, reason);
}

internal static partial class ConductorSelfRelaunch
{
    private static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultSelfCheckTimeout = TimeSpan.FromSeconds(30);

    internal static Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult> Create(
        ConductorSelfRelaunchOptions options,
        Func<ConductorSuccessorStagingOptions, ConductorPreparedSuccessor>? prepare = null,
        Func<ConductLoopHandoffOptions, ConductorLoopHandoffRequest, ConductorLoopHandoffResult>? handoff = null) =>
        request => TryRelaunch(
            options,
            request,
            prepare ?? (stagingOptions => PrepareSuccessor(stagingOptions)),
            handoff ?? ((handoffOptions, handoffRequest) =>
                ConductorLoopHandoff.Create(handoffOptions)(handoffRequest)));

    internal static ConductorSelfRelaunchResult TryRelaunch(
        ConductorSelfRelaunchOptions options,
        ConductorSelfRelaunchRequest request,
        Func<ConductorSuccessorStagingOptions, ConductorPreparedSuccessor> prepare,
        Func<ConductLoopHandoffOptions, ConductorLoopHandoffRequest, ConductorLoopHandoffResult> handoff)
    {
        ConductorPreparedSuccessor successor;
        try
        {
            successor = prepare(options.Staging);
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

        try
        {
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
                    successor,
                    handoffResult.RollbackSucceeded);
        }
        finally
        {
            successor.RunDirectoryLease?.Dispose();
        }
    }

    internal static ConductorPreparedSuccessor PrepareSuccessor(
        ConductorSuccessorStagingOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            TimeSpan.FromSeconds(30),
            cancellationToken);
        EnsureSucceeded("build", "resolve merged git HEAD", gitHeadResult);
        var gitHead = gitHeadResult.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(gitHead))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "build",
                "Merged git HEAD was empty.");
        }

        var fromStore = options.PrebuiltAppOutputDirectory is null && options.LandingAppBuildStore is not null;
        if (fromStore)
        {
            try
            {
                options = options with
                {
                    PrebuiltAppOutputDirectory = options.LandingAppBuildStore!.GetOrBuild(
                        options.RepositoryRoot, gitHead, buildTimeout, cancellationToken)
                };
            }
            catch (LandingAppBuildFailedException ex)
            {
                EnsureSucceeded("build", "build merged conductor", new CapturedProcessResult(
                    ex.Result.ExitCode, ex.Result.Stdout, ex.Result.Stderr, ex.Result.TimedOut));
                throw;
            }
        }

        var buildOutputDirectory = Path.Combine(
            Path.GetDirectoryName(options.AppDllPath)
                ?? throw new ConductorSelfRelaunchPreparationException("build", "App output directory was not configured."),
            $"{gitHead}-{Guid.NewGuid():N}");
        var appDllPath = Path.Combine(buildOutputDirectory, Path.GetFileName(options.AppDllPath));
        var appHeadMarkerPath = appDllPath + ".git-head";
        FileStream? runDirectoryLease = null;
        try
        {
        var build = ProduceBuildOutput(options, buildOutputDirectory, buildTimeout, cancellationToken, fromStore);
        EnsureSucceeded("build", "build merged conductor", build);

        var marker = RunPowerShell(
            options,
            options.UpdateHeadMarkerScriptPath,
            [options.RepositoryRoot, appHeadMarkerPath],
            TimeSpan.FromMinutes(1),
            cancellationToken);
        EnsureSucceeded("stage", "record conductor git HEAD", marker);

        var resolve = RunPowerShell(
            options,
            options.ResolveRunDirectoryScriptPath,
            [appDllPath],
            TimeSpan.FromMinutes(2),
            cancellationToken);
        EnsureSucceeded("stage", "publish content-addressed run directory", resolve);
        var runDirectory = resolve.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "stage",
                "Run-directory resolver returned no content-addressed directory.");
        }

        var successorDll = Path.Combine(runDirectory, Path.GetFileName(appDllPath));
        runDirectoryLease = File.Open(successorDll, FileMode.Open, FileAccess.Read, FileShare.Read);
        WholeDirectoryRemoval.Remove(buildOutputDirectory, Path.GetFileName(appDllPath));
        var stagedSourceCommit = ReadStagedSourceCommit(
            runDirectory,
            Path.GetFileName(appDllPath),
            gitHead);
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
            selfCheckTimeout,
            cancellationToken);
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

        var prepared = new ConductorPreparedSuccessor(
            runDirectory,
            successorDll,
            gitHead,
            stagedSourceCommit,
            readiness,
            runDirectoryLease);
        runDirectoryLease = null;
        return prepared;
        }
        finally
        {
            runDirectoryLease?.Dispose();
            WholeDirectoryRemoval.Remove(buildOutputDirectory, Path.GetFileName(appDllPath));
            var repositoryBuildRoot = Path.GetDirectoryName(buildOutputDirectory)!;
            WholeDirectoryRemoval.TryRemoveEmptyDirectory(repositoryBuildRoot);
            WholeDirectoryRemoval.TryRemoveEmptyDirectory(Path.GetDirectoryName(repositoryBuildRoot)!);
        }
    }

    internal static string ReadStagedSourceCommit(
        string runDirectory,
        string dllFileName,
        string expectedRepositoryHead)
    {
        var markerPath = Path.Combine(runDirectory, dllFileName + ".git-head");
        string stagedSourceCommit;
        try
        {
            stagedSourceCommit = File.ReadAllText(markerPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConductorSelfRelaunchPreparationException(
                "stage",
                $"Staged source marker could not be read at {markerPath}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(stagedSourceCommit))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "stage",
                $"Staged source marker was empty at {markerPath}.");
        }

        if (!stagedSourceCommit.Equals(expectedRepositoryHead, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "stage",
                $"Staged source commit {stagedSourceCommit} did not match repository HEAD {expectedRepositoryHead}.");
        }

        return stagedSourceCommit;
    }

    private static CapturedProcessResult RunPowerShell(
        ConductorSuccessorStagingOptions options,
        string scriptPath,
        IReadOnlyList<string> scriptArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
        return RunProcess(
            options.PowerShellPath,
            arguments,
            options.RepositoryRoot,
            timeout,
            cancellationToken);
    }

    private static CapturedProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
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
        var stdoutDrain = PipeDrain.Start(process.StandardOutput, "self-relaunch-stdout-drain");
        var stderrDrain = PipeDrain.Start(process.StandardError, "self-relaunch-stderr-drain");
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token,
            cancellationToken);
        try
        {
            process.WaitForExitAsync(linkedCts.Token).GetAwaiter().GetResult();
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

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new CapturedProcessResult(
                -1,
                stdoutDrain.Text,
                stderrDrain.Text,
                TimedOut: true);
        }

        var drainDeadline = Environment.TickCount64 + PipeDrain.DefaultTimeoutMilliseconds;
        var outputDrained = stdoutDrain.Join(drainDeadline);
        var errorDrained = stderrDrain.Join(drainDeadline);
        var stderr = stderrDrain.Text;
        if (!outputDrained || !errorDrained)
        {
            stderr = PipeDrain.AppendDiagnostic(
                stderr,
                PipeDrain.DescribeTimeout(
                    "conductor self-relaunch",
                    PipeDrain.DefaultTimeoutMilliseconds,
                    stdoutDrain,
                    stderrDrain));
        }

        return new CapturedProcessResult(
            process.ExitCode,
            stdoutDrain.Text,
            stderr,
            TimedOut: !outputDrained || !errorDrained);
    }

    internal static (int ExitCode, string Stdout, string Stderr, bool TimedOut) RunProcessForTests(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var result = RunProcess(fileName, arguments, workingDirectory, timeout);
        return (result.ExitCode, result.Stdout, result.Stderr, result.TimedOut);
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
