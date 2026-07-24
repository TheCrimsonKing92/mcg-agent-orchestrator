using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorLoopLease : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private ConductorLoopLease(FileStream stream)
    {
        _stream = stream;
    }

    public static ConductorLoopLease Acquire(string orchestratorDirectory)
    {
        Directory.CreateDirectory(orchestratorDirectory);
        var path = Path.Combine(orchestratorDirectory, "conduct-loop.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.WriteLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.Flush();
            stream.Flush();
            stream.Position = 0;
            return new ConductorLoopLease(stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"conduct loop is already running or the conduct-loop lock is held: {path}",
                ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stream.Dispose();
    }
}

internal sealed class ConductorLoopLeaseController : IDisposable
{
    private readonly string _orchestratorDirectory;
    private ConductorLoopLease? _lease;

    private ConductorLoopLeaseController(string orchestratorDirectory)
    {
        _orchestratorDirectory = orchestratorDirectory;
        _lease = ConductorLoopLease.Acquire(orchestratorDirectory);
    }

    public static ConductorLoopLeaseController Acquire(string orchestratorDirectory) =>
        new(orchestratorDirectory);

    public void Release()
    {
        _lease?.Dispose();
        _lease = null;
    }

    public void Reacquire()
    {
        _lease ??= ConductorLoopLease.Acquire(_orchestratorDirectory);
    }

    public bool IsHeld => _lease is not null;

    public void Dispose() => Release();
}

internal sealed record ConductLoopHandoffOptions(
    IReadOnlyList<string> Args,
    string ExecutionDirectory,
    string OrchestratorDirectory,
    string LogDirectory,
    string RunEventStorePath,
    string StopFilePath,
    int RenewalCount,
    int MaxRenewals,
    Action ReleaseCurrentLease,
    TimeSpan VerificationTimeout = default,
    TimeSpan VerificationHardTimeout = default,
    Func<ConductLoopHandoffOptions, long, bool>? LoopStartProbe = null,
    Func<ConductLoopLaunchResult, ConductLoopLaunchRequest, bool>? SuccessorReadyProbe = null,
    Action? ReacquireCurrentLease = null,
    Action<int>? StopFailedSuccessor = null,
    IReadOnlyList<string>? SuccessorCommandPrefix = null);

internal sealed record ConductLoopLaunchRequest(
    string Name,
    IReadOnlyList<string> Args,
    string StdoutPath,
    string StderrPath,
    string WorkingDirectory,
    int RenewalCount,
    IReadOnlyList<string>? CommandPrefix = null,
    string? ReadyFilePath = null,
    string? ActivationFilePath = null,
    string? HandoffToken = null,
    int? IncumbentProcessId = null,
    int AuthorityWaitTimeoutSeconds = 0);

internal sealed record ConductLoopLaunchResult(
    int ProcessId,
    string StdoutPath,
    string StderrPath,
    string LaunchDetail = "");

internal sealed record ConductLoopHandoffVerification(
    bool ProcessAlive,
    bool StdoutLogExists,
    bool LoopStartJournaled,
    string Detail,
    string TerminalReason = "")
{
    public bool Succeeded => LoopStartJournaled;
}

internal static partial class ConductorLoopHandoff
{
    public const string RenewalCountFlag = "--handoff-renewals";
    public const int DefaultMaxRenewalsWithoutLanding = 6;
    public static readonly TimeSpan DefaultVerificationTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan DefaultVerificationHardTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan VerificationPollInterval = TimeSpan.FromMilliseconds(250);
    private const string BatchNameEnvironmentVariable = "MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME";
    private const string RenewalCountEnvironmentVariable = "MCG_ORCHESTRATOR_HANDOFF_RENEWALS";
    private const string ReadyPathEnvironmentVariable = "MCG_ORCHESTRATOR_HANDOFF_READY_PATH";
    private const string ActivationPathEnvironmentVariable = "MCG_ORCHESTRATOR_HANDOFF_ACTIVATE_PATH";
    private const string HandoffTokenEnvironmentVariable = "MCG_ORCHESTRATOR_HANDOFF_TOKEN";
    private const string IncumbentPidEnvironmentVariable = "MCG_ORCHESTRATOR_HANDOFF_INCUMBENT_PID";
    private const string AuthorityWaitTimeoutEnvironmentVariable = "MCG_ORCHESTRATOR_HANDOFF_WAIT_SECONDS";
    private static readonly Regex TimestampedOperatorLogName = new(
        @"^operator-(?<name>.+)-\d{14}\.(out|err)\.log$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult> Create(
        ConductLoopHandoffOptions options,
        Func<ConductLoopLaunchRequest, ConductLoopLaunchResult>? launch = null,
        Func<ConductLoopLaunchResult, long, ConductLoopHandoffVerification>? verify = null) =>
        request => TryStartSuccessor(options, request, launch ?? LaunchDetached, verify ?? ((result, eventCursor) => VerifySuccessor(result, options, eventCursor)));

    internal static ConductorLoopHandoffResult TryStartSuccessor(
        ConductLoopHandoffOptions options,
        ConductorLoopHandoffRequest request,
        Func<ConductLoopLaunchRequest, ConductLoopLaunchResult> launch,
        Func<ConductLoopLaunchResult, long, ConductLoopHandoffVerification>? verify = null)
    {
        if (File.Exists(options.StopFilePath))
        {
            TryRecordHandoffEvent(options.RunEventStorePath, "Skipped", "stop-file");
            return ConductorLoopHandoffResult.Skipped("stop-file");
        }

        var nextRenewalCount = request.LandedGoalDelta > 0 ? 0 : options.RenewalCount + 1;
        if (nextRenewalCount > options.MaxRenewals)
        {
            var reason = $"renewal-cap count={nextRenewalCount} max={options.MaxRenewals}";
            TryRecordHandoffEvent(options.RunEventStorePath, "Escalated", reason);
            return ConductorLoopHandoffResult.Skipped(reason);
        }

        Directory.CreateDirectory(options.LogDirectory);
        var successorName = ResolveSuccessorName();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var safeName = ToSafeName(successorName);
        var stdoutPath = Path.GetFullPath(Path.Combine(options.LogDirectory, $"operator-{safeName}-{stamp}.out.log"));
        var stderrPath = Path.GetFullPath(Path.Combine(options.LogDirectory, $"operator-{safeName}-{stamp}.err.log"));
        var args = WithRenewalCount(options.Args, nextRenewalCount);
        var handoffDirectory = Path.Combine(
            options.OrchestratorDirectory,
            "handoff",
            $"{stamp}-{Guid.NewGuid():N}");
        var readyFilePath = Path.Combine(handoffDirectory, "ready");
        var activationFilePath = Path.Combine(handoffDirectory, "activate");
        var handoffToken = Guid.NewGuid().ToString("N");
        var authorityWaitTimeout = options.VerificationHardTimeout <= TimeSpan.Zero
            ? DefaultVerificationHardTimeout
            : options.VerificationHardTimeout;
        var launchRequest = new ConductLoopLaunchRequest(
            successorName,
            args,
            stdoutPath,
            stderrPath,
            options.ExecutionDirectory,
            nextRenewalCount,
            options.SuccessorCommandPrefix,
            readyFilePath,
            activationFilePath,
            handoffToken,
            Environment.ProcessId,
            (int)Math.Ceiling(authorityWaitTimeout.TotalSeconds));

        var guardDetail = "guard=incumbent-held-until-successor-ready";
        verify ??= (result, eventCursor) => VerifySuccessor(result, options, eventCursor);
        const int attempt = 1;
        var eventCursor = GetConductEventCursor(options);
        ConductLoopLaunchResult? launched = null;
        var authorityReleased = false;
        try
        {
            Directory.CreateDirectory(handoffDirectory);
            launched = launch(launchRequest);
            var readyProbe = options.SuccessorReadyProbe ?? HasSuccessorReadySignal;
            var readiness = WaitForSuccessorReady(launched, launchRequest, options, readyProbe);
            if (!readiness.Succeeded)
            {
                var preHandoffDetail =
                    $"attempt={attempt} pid={launched.ProcessId} stdout={launched.StdoutPath} stderr={launched.StderrPath} " +
                    $"{guardDetail} readiness={readiness.Detail}";
                return FailAndRollback(
                    options,
                    launchRequest,
                    launched,
                    authorityReleased,
                    VerificationFailureReason(readiness),
                    preHandoffDetail);
            }

            options.ReleaseCurrentLease();
            authorityReleased = true;
            File.WriteAllText(activationFilePath, handoffToken);
            var verification = verify(launched, eventCursor);
            var launchDetail = string.IsNullOrWhiteSpace(launched.LaunchDetail)
                ? "spawnPath=injected breakawayRequested=false breakawaySucceeded=not-applicable"
                : launched.LaunchDetail;
            var handoffDetail =
                $"{guardDetail} readiness={readiness.Detail} authorityReleasedAfterReady=true {launchDetail} verification={verification.Detail}";
            var detail =
                $"attempt={attempt} pid={launched.ProcessId} stdout={launched.StdoutPath} stderr={launched.StderrPath} {handoffDetail}";

            if (verification.Succeeded)
            {
                TryRecordHandoffEvent(options.RunEventStorePath, "Started", detail);
                return ConductorLoopHandoffResult.StartedProcess(
                    launched.ProcessId,
                    launched.StdoutPath,
                    launched.StderrPath,
                    handoffDetail);
            }

            return FailAndRollback(
                options,
                launchRequest,
                launched,
                authorityReleased,
                VerificationFailureReason(verification),
                detail);
        }
        catch (Exception ex)
        {
            var launchDetail = launched is null
                ? DefaultFailedLaunchDetail()
                : string.IsNullOrWhiteSpace(launched.LaunchDetail)
                    ? "spawnPath=injected breakawayRequested=false breakawaySucceeded=not-applicable"
                    : launched.LaunchDetail;
            var pidDetail = launched is null ? "" : $" pid={launched.ProcessId}";
            var detail =
                $"attempt={attempt}{pidDetail} stdout={launchRequest.StdoutPath} stderr={launchRequest.StderrPath} " +
                $"{guardDetail} {launchDetail} error={ex.GetType().Name}:{ex.Message}";
            var failureReason =
                $"successor-{(launched is null ? "launch" : "handoff")}-failed {ex.GetType().Name}:{ex.Message}";
            return FailAndRollback(
                options,
                launchRequest,
                launched,
                authorityReleased,
                failureReason,
                detail);
        }
        finally
        {
            TryDeleteHandoffDirectory(handoffDirectory);
        }
    }

    private static ConductorLoopHandoffResult FailAndRollback(
        ConductLoopHandoffOptions options,
        ConductLoopLaunchRequest request,
        ConductLoopLaunchResult? launched,
        bool authorityReleased,
        string failureReason,
        string detail)
    {
        TryRecordHandoffEvent(options.RunEventStorePath, "Failed", detail);
        EmitHandoffFailure(detail);

        Exception? stopFailure = null;
        if (launched is not null)
        {
            try
            {
                (options.StopFailedSuccessor ?? StopFailedSuccessor).Invoke(launched.ProcessId);
            }
            catch (Exception ex)
            {
                stopFailure = ex;
                if (options.StopFailedSuccessor is not null)
                {
                    try
                    {
                        StopFailedSuccessor(launched.ProcessId);
                        stopFailure = null;
                    }
                    catch (Exception fallbackEx)
                    {
                        stopFailure = new AggregateException(ex, fallbackEx);
                    }
                }
            }
        }

        Exception? reacquireFailure = null;
        if (authorityReleased)
        {
            try
            {
                options.ReacquireCurrentLease?.Invoke();
            }
            catch (Exception ex)
            {
                reacquireFailure = ex;
            }
        }

        var rollbackSucceeded = stopFailure is null && reacquireFailure is null;
        var rollbackErrors = string.Join(
            ";",
            new[]
            {
                stopFailure is null ? null : $"stop={stopFailure.GetType().Name}:{stopFailure.Message}",
                reacquireFailure is null ? null : $"reacquire={reacquireFailure.GetType().Name}:{reacquireFailure.Message}"
            }.Where(value => value is not null));
        var rollbackDetail =
            $"{detail} rollbackSucceeded={ToLowerInvariant(rollbackSucceeded)} " +
            $"successorStopped={ToLowerInvariant(launched is null || stopFailure is null)} " +
            $"authorityReacquired={ToLowerInvariant(!authorityReleased || reacquireFailure is null)} " +
            $"continuing={ToLowerInvariant(rollbackSucceeded)}" +
            (rollbackErrors.Length == 0 ? "" : $" rollbackErrors={rollbackErrors}");
        TryRecordHandoffEvent(options.RunEventStorePath, "Escalated",
            $"reason={failureReason} stdout={request.StdoutPath} stderr={request.StderrPath} verification={rollbackDetail}");
        return ConductorLoopHandoffResult.FailedStart(
            failureReason,
            request.StdoutPath,
            request.StderrPath,
            rollbackDetail,
            launched?.ProcessId);
    }

    private static ConductLoopHandoffVerification WaitForSuccessorReady(
        ConductLoopLaunchResult result,
        ConductLoopLaunchRequest request,
        ConductLoopHandoffOptions options,
        Func<ConductLoopLaunchResult, ConductLoopLaunchRequest, bool> readyProbe)
    {
        var timeout = options.VerificationTimeout <= TimeSpan.Zero
            ? DefaultVerificationTimeout
            : options.VerificationTimeout;
        var hardTimeout = options.VerificationHardTimeout <= TimeSpan.Zero
            ? DefaultVerificationHardTimeout
            : options.VerificationHardTimeout;
        if (hardTimeout < timeout)
            hardTimeout = timeout;

        var started = DateTimeOffset.UtcNow;
        var pendingDeadline = started.Add(timeout);
        var hardDeadline = started.Add(hardTimeout);
        var pendingEmitted = false;
        var processAlive = false;
        var ready = false;
        var terminalReason = "timeout";
        var elapsed = TimeSpan.Zero;

        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            elapsed = now - started;
            processAlive = IsProcessAlive(result.ProcessId);
            ready = readyProbe(result, request);
            if (ready)
            {
                terminalReason = "successor-ready";
                break;
            }
            if (!processAlive)
            {
                terminalReason = "child-dead";
                break;
            }
            if (now >= hardDeadline)
            {
                terminalReason = "alive-timeout";
                break;
            }

            if (!pendingEmitted && now >= pendingDeadline)
            {
                pendingEmitted = true;
                EmitHandoffPending(
                    options,
                    $"phase=successor-readiness processAlive=true successorReady=false " +
                    $"elapsedSeconds={(int)Math.Max(0, elapsed.TotalSeconds)}");
            }

            Thread.Sleep(VerificationPollInterval);
        }

        return new ConductLoopHandoffVerification(
            processAlive,
            File.Exists(result.StdoutPath),
            ready,
            $"phase=successor-readiness processAlive={ToLowerInvariant(processAlive)} " +
            $"successorReady={ToLowerInvariant(ready)} terminalReason={terminalReason} " +
            $"elapsedSeconds={(int)Math.Max(0, elapsed.TotalSeconds)}",
            terminalReason);
    }

    private static bool HasSuccessorReadySignal(
        ConductLoopLaunchResult result,
        ConductLoopLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReadyFilePath) ||
            string.IsNullOrWhiteSpace(request.HandoffToken) ||
            !File.Exists(request.ReadyFilePath))
        {
            return false;
        }

        try
        {
            var expected =
                $"LOOP_HANDOFF_READY token={request.HandoffToken} pid={result.ProcessId}";
            return string.Equals(
                File.ReadAllText(request.ReadyFilePath).Trim(),
                expected,
                StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static bool WaitForAuthorityTransferIfRequested()
    {
        var readyPath = Environment.GetEnvironmentVariable(ReadyPathEnvironmentVariable);
        var activationPath = Environment.GetEnvironmentVariable(ActivationPathEnvironmentVariable);
        var token = Environment.GetEnvironmentVariable(HandoffTokenEnvironmentVariable);
        var incumbentPidText = Environment.GetEnvironmentVariable(IncumbentPidEnvironmentVariable);
        var waitSecondsText = Environment.GetEnvironmentVariable(AuthorityWaitTimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(readyPath) &&
            string.IsNullOrWhiteSpace(activationPath) &&
            string.IsNullOrWhiteSpace(token) &&
            string.IsNullOrWhiteSpace(incumbentPidText) &&
            string.IsNullOrWhiteSpace(waitSecondsText))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(readyPath) ||
            string.IsNullOrWhiteSpace(activationPath) ||
            string.IsNullOrWhiteSpace(token) ||
            !int.TryParse(incumbentPidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var incumbentPid) ||
            incumbentPid <= 0 ||
            !int.TryParse(waitSecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var waitSeconds) ||
            waitSeconds <= 0)
        {
            throw new InvalidOperationException(
                "Incomplete conductor handoff environment; ready path, activation path, token, incumbent pid, and wait timeout are all required.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(readyPath) ?? ".");
        var readyContent = $"LOOP_HANDOFF_READY token={token} pid={Environment.ProcessId}";
        var temporaryReadyPath = readyPath + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporaryReadyPath, readyContent);
        File.Move(temporaryReadyPath, readyPath, overwrite: true);
        Console.WriteLine(readyContent);
        Console.Out.Flush();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            if (File.Exists(activationPath))
            {
                var activationToken = File.ReadAllText(activationPath).Trim();
                if (!string.Equals(activationToken, token, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Conductor handoff activation token did not match the readiness token.");
                }

                TryDeleteFile(readyPath);
                TryDeleteFile(activationPath);
                return true;
            }

            if (!IsProcessAlive(incumbentPid))
            {
                throw new InvalidOperationException(
                    $"Incumbent conductor process {incumbentPid} exited before authority transfer.");
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {waitSeconds} seconds waiting for conductor authority transfer.");
            }

            Thread.Sleep(VerificationPollInterval);
        }
    }

    internal static bool IsAuthorityTransferRequested =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReadyPathEnvironmentVariable)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ActivationPathEnvironmentVariable)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HandoffTokenEnvironmentVariable)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IncumbentPidEnvironmentVariable)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AuthorityWaitTimeoutEnvironmentVariable));

    private static void TryDeleteHandoffDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string VerificationFailureReason(ConductLoopHandoffVerification verification) =>
        verification.TerminalReason switch
        {
            "child-dead" => "successor-child-dead",
            "alive-timeout" => "successor-alive-timeout",
            "timeout" => "successor-timeout",
            _ => "successor-verification-failed"
        };

    private static string DefaultFailedLaunchDetail() =>
        OperatingSystem.IsWindows()
            ? "spawnPath=windows-createprocess breakawayRequested=true breakawaySucceeded=false"
            : "spawnPath=posix-shell-detached breakawayRequested=false breakawaySucceeded=not-applicable";

    internal static int ParseRenewalCount(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(RenewalCountFlag, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return Math.Max(0, count);
            }
        }

        if (int.TryParse(
                Environment.GetEnvironmentVariable(RenewalCountEnvironmentVariable),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var environmentCount))
        {
            return Math.Max(0, environmentCount);
        }

        return 0;
    }

    internal static IReadOnlyList<string> WithRenewalCount(IReadOnlyList<string> args, int renewalCount)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals(RenewalCountFlag, StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            result.Add(args[i]);
        }

        return result;
    }

    internal static string ResolveSuccessorName(string? currentName = null, string? stdoutPath = null)
    {
        currentName ??= Environment.GetEnvironmentVariable(BatchNameEnvironmentVariable);
        currentName = string.IsNullOrWhiteSpace(currentName)
            ? TryExtractNameFromLogPath(stdoutPath ?? Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH"))
            : currentName;
        currentName = string.IsNullOrWhiteSpace(currentName) ? "conduct-loop" : currentName;

        var match = Regex.Match(currentName, @"^(?<prefix>.*?)(?<number>\d+)$", RegexOptions.CultureInvariant);
        if (!match.Success)
            return currentName + "-handoff-1";

        var digits = match.Groups["number"].Value;
        var next = long.Parse(digits, CultureInfo.InvariantCulture) + 1;
        return match.Groups["prefix"].Value + next.ToString(new string('0', digits.Length), CultureInfo.InvariantCulture);
    }

    private static string? TryExtractNameFromLogPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var match = TimestampedOperatorLogName.Match(Path.GetFileName(path));
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static ConductLoopLaunchResult LaunchDetached(ConductLoopLaunchRequest request)
    {
        var command = request.CommandPrefix?.ToList() ?? [];
        if (command.Count == 0)
        {
            var commandLineArgs = Environment.GetCommandLineArgs();
            var executable = Environment.ProcessPath ?? "dotnet";
            command.Add(executable);
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                commandLineArgs.Length > 0)
            {
                command.Add(commandLineArgs[0]);
            }
        }

        command.AddRange(request.Args);
        if (OperatingSystem.IsWindows())
            return LaunchDetachedWindows(request, command);

        var commandLine = string.Join(" ", command.Select(QuoteCommandArgument)) +
            " 1>" + QuoteCommandArgument(request.StdoutPath) +
            " 2>" + QuoteCommandArgument(request.StderrPath);
        var shell = "/bin/sh";
        var shellArgs = new[] { "-c", commandLine + " & echo $!" };

        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true
        };

        foreach (var arg in shellArgs)
            startInfo.ArgumentList.Add(arg);

        startInfo.Environment["MCG_ORCHESTRATOR_STDOUT_LOG_PATH"] = request.StdoutPath;
        startInfo.Environment["MCG_ORCHESTRATOR_STDERR_LOG_PATH"] = request.StderrPath;
        startInfo.Environment[BatchNameEnvironmentVariable] = request.Name;
        startInfo.Environment[RenewalCountEnvironmentVariable] = request.RenewalCount.ToString(CultureInfo.InvariantCulture);
        AddHandoffEnvironment(startInfo.Environment, request);
        startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = request.WorkingDirectory;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start conduct loop successor.");
        var pidText = process.StandardOutput.ReadLine();
        if (!process.WaitForExit(5000) || process.ExitCode != 0)
            throw new InvalidOperationException("Detached conduct loop launcher did not exit cleanly.");
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            throw new InvalidOperationException("Detached conduct loop launcher did not report a successor pid.");

        return new ConductLoopLaunchResult(
            pid,
            request.StdoutPath,
            request.StderrPath,
            "spawnPath=posix-shell-detached breakawayRequested=false breakawaySucceeded=not-applicable");
    }

    internal static ConductLoopLaunchResult LaunchDetachedWindows(ConductLoopLaunchRequest request, IReadOnlyList<string> command)
    {
        var commandLine = new StringBuilder(BuildWindowsProcessCommandLine(command));
        var environment = BuildWindowsEnvironmentBlock(request);
        var stdoutHandle = IntPtr.Zero;
        var stderrHandle = IntPtr.Zero;

        try
        {
            stdoutHandle = CreateInheritedOutputFile(request.StdoutPath);
            stderrHandle = CreateInheritedOutputFile(request.StderrPath);
            var startupInfo = new WindowsProcessStartupInfoEx
            {
                cb = Marshal.SizeOf<WindowsProcessStartupInfoEx>(),
                dwFlags = (int)WindowsStartupInfoFlags.UseStdHandles,
                hStdOutput = stdoutHandle,
                hStdError = stderrHandle
            };
            var attributeList = CreateInheritedHandleList(stdoutHandle, stderrHandle);
            startupInfo.lpAttributeList = attributeList.AttributeList;

            try
            {
                if (!CreateProcessW(
                        lpApplicationName: command[0],
                        lpCommandLine: commandLine,
                        lpProcessAttributes: IntPtr.Zero,
                        lpThreadAttributes: IntPtr.Zero,
                        bInheritHandles: true,
                        dwCreationFlags: WindowsCreationFlags.CreateBreakawayFromJob |
                            WindowsCreationFlags.CreateNewProcessGroup |
                            WindowsCreationFlags.CreateUnicodeEnvironment |
                            WindowsCreationFlags.ExtendedStartupInfoPresent,
                        lpEnvironment: environment,
                        lpCurrentDirectory: request.WorkingDirectory,
                        lpStartupInfo: ref startupInfo,
                        lpProcessInformation: out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to start breakaway conduct loop successor.");
                }

                try
                {
                    var inJob = IsProcessInJob(processInformation.hProcess);
                    if (inJob)
                    {
                        TerminateProcess(processInformation.hProcess, 1);
                        throw new InvalidOperationException("Breakaway conduct loop successor remained in a Windows job.");
                    }

                    return new ConductLoopLaunchResult(
                        (int)processInformation.dwProcessId,
                        request.StdoutPath,
                        request.StderrPath,
                        "spawnPath=windows-createprocess breakawayRequested=true breakawaySucceeded=true");
                }
                finally
                {
                    CloseHandle(processInformation.hThread);
                    CloseHandle(processInformation.hProcess);
                }
            }
            finally
            {
                attributeList.Dispose();
            }
        }
        finally
        {
            if (stdoutHandle != IntPtr.Zero)
                CloseHandle(stdoutHandle);
            if (stderrHandle != IntPtr.Zero)
                CloseHandle(stderrHandle);
        }
    }

    private static WindowsInheritedHandleList CreateInheritedHandleList(params IntPtr[] handles)
    {
        var size = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to size Windows process attribute list.");

        var attributeList = Marshal.AllocHGlobal(size);
        var handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize Windows process attribute list.");

            initialized = true;
            Marshal.Copy(handles, 0, handleList, handles.Length);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    new IntPtr(ProcThreadAttributeHandleList),
                    handleList,
                    new IntPtr(IntPtr.Size * handles.Length),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to restrict inherited Windows process handles.");
            }

            return new WindowsInheritedHandleList(attributeList, handleList, initialized);
        }
        catch
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    internal static string BuildWindowsProcessCommandLine(IReadOnlyList<string> command) =>
        string.Join(" ", command.Select(QuoteCommandArgument));

    internal static string BuildWindowsBreakawayCommandLine(
        string cmdPath,
        IReadOnlyList<string> command,
        string stdoutPath,
        string stderrPath)
    {
        var innerCommand = string.Join(" ", command.Select(QuoteCommandArgument)) +
            " 1>" + QuoteCommandArgument(stdoutPath) +
            " 2>" + QuoteCommandArgument(stderrPath);
        return QuoteCommandArgument(cmdPath) + " /d /s /c \"" + innerCommand + "\"";
    }

    private static IntPtr CreateInheritedOutputFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var securityAttributes = new WindowsSecurityAttributes
        {
            nLength = Marshal.SizeOf<WindowsSecurityAttributes>(),
            bInheritHandle = true
        };

        var handle = CreateFileW(
            path,
            WindowsFileAccess.GenericWrite,
            WindowsFileShare.Read | WindowsFileShare.Delete,
            ref securityAttributes,
            WindowsCreationDisposition.CreateAlways,
            WindowsFileFlags.FileAttributeNormal,
            IntPtr.Zero);
        if (handle == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open conduct loop successor log file: {path}");

        return handle;
    }

    private static string BuildWindowsEnvironmentBlock(ConductLoopLaunchRequest request)
    {
        var values = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string?)entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        values["MCG_ORCHESTRATOR_STDOUT_LOG_PATH"] = request.StdoutPath;
        values["MCG_ORCHESTRATOR_STDERR_LOG_PATH"] = request.StderrPath;
        values[BatchNameEnvironmentVariable] = request.Name;
        values[RenewalCountEnvironmentVariable] = request.RenewalCount.ToString(CultureInfo.InvariantCulture);
        values[OrchestratorWorkspace.RepoRootEnvironmentVariable] = request.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(request.ReadyFilePath))
            values[ReadyPathEnvironmentVariable] = request.ReadyFilePath;
        if (!string.IsNullOrWhiteSpace(request.ActivationFilePath))
            values[ActivationPathEnvironmentVariable] = request.ActivationFilePath;
        if (!string.IsNullOrWhiteSpace(request.HandoffToken))
            values[HandoffTokenEnvironmentVariable] = request.HandoffToken;
        if (request.IncumbentProcessId is { } incumbentProcessId)
            values[IncumbentPidEnvironmentVariable] = incumbentProcessId.ToString(CultureInfo.InvariantCulture);
        if (request.AuthorityWaitTimeoutSeconds > 0)
            values[AuthorityWaitTimeoutEnvironmentVariable] =
                request.AuthorityWaitTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key);
            builder.Append('=');
            builder.Append(pair.Value);
            builder.Append('\0');
        }

        builder.Append('\0');
        return builder.ToString();
    }

    private static void AddHandoffEnvironment(
        IDictionary<string, string?> environment,
        ConductLoopLaunchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ReadyFilePath))
            environment[ReadyPathEnvironmentVariable] = request.ReadyFilePath;
        if (!string.IsNullOrWhiteSpace(request.ActivationFilePath))
            environment[ActivationPathEnvironmentVariable] = request.ActivationFilePath;
        if (!string.IsNullOrWhiteSpace(request.HandoffToken))
            environment[HandoffTokenEnvironmentVariable] = request.HandoffToken;
        if (request.IncumbentProcessId is { } incumbentProcessId)
            environment[IncumbentPidEnvironmentVariable] = incumbentProcessId.ToString(CultureInfo.InvariantCulture);
        if (request.AuthorityWaitTimeoutSeconds > 0)
            environment[AuthorityWaitTimeoutEnvironmentVariable] =
                request.AuthorityWaitTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsProcessInJob(IntPtr processHandle)
    {
        if (!IsProcessInJob(processHandle, IntPtr.Zero, out var result))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to verify breakaway conduct loop successor job membership.");

        return result;
    }

    private static ConductLoopHandoffVerification VerifySuccessor(
        ConductLoopLaunchResult result,
        ConductLoopHandoffOptions options,
        long eventCursor)
    {
        var timeout = options.VerificationTimeout <= TimeSpan.Zero
            ? DefaultVerificationTimeout
            : options.VerificationTimeout;
        var hardTimeout = options.VerificationHardTimeout <= TimeSpan.Zero
            ? DefaultVerificationHardTimeout
            : options.VerificationHardTimeout;
        if (hardTimeout < timeout)
            hardTimeout = timeout;

        var started = DateTimeOffset.UtcNow;
        var pendingDeadline = started.Add(timeout);
        var hardDeadline = started.Add(hardTimeout);
        var processAlive = false;
        var stdoutLogExists = false;
        var loopStartJournaled = false;
        var terminalReason = "timeout";
        var pendingEmitted = false;
        var elapsed = TimeSpan.Zero;

        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            elapsed = now - started;
            processAlive = IsProcessAlive(result.ProcessId);
            stdoutLogExists = File.Exists(result.StdoutPath);
            loopStartJournaled = (options.LoopStartProbe ?? HasLoopStartAfterCursor)(options, eventCursor);
            if (loopStartJournaled)
            {
                terminalReason = "loop-start";
                break;
            }
            if (!processAlive)
            {
                terminalReason = "child-dead";
                break;
            }
            if (now >= hardDeadline)
            {
                terminalReason = "alive-timeout";
                break;
            }

            if (!pendingEmitted && now >= pendingDeadline)
            {
                pendingEmitted = true;
                EmitHandoffPending(options, FormatVerificationDetail(
                    processAlive,
                    stdoutLogExists,
                    loopStartJournaled,
                    "pending",
                    elapsed,
                    timeout,
                    hardTimeout));
            }

            Thread.Sleep(VerificationPollInterval);
        }

        var detail = FormatVerificationDetail(
            processAlive,
            stdoutLogExists,
            loopStartJournaled,
            terminalReason,
            elapsed,
            timeout,
            hardTimeout);
        return new ConductLoopHandoffVerification(processAlive, stdoutLogExists, loopStartJournaled, detail, terminalReason);
    }

    private static string FormatVerificationDetail(
        bool processAlive,
        bool stdoutLogExists,
        bool loopStartJournaled,
        string terminalReason,
        TimeSpan elapsed,
        TimeSpan timeout,
        TimeSpan hardTimeout) =>
        $"processAlive={ToLowerInvariant(processAlive)} " +
        $"stdoutLogExists={ToLowerInvariant(stdoutLogExists)} " +
        $"loopStartJournaled={ToLowerInvariant(loopStartJournaled)} " +
        $"terminalReason={terminalReason} " +
        $"elapsedSeconds={(int)Math.Max(0, elapsed.TotalSeconds)} " +
        $"legacyTimeoutSeconds={(int)Math.Ceiling(timeout.TotalSeconds)} " +
        $"hardTimeoutSeconds={(int)Math.Ceiling(hardTimeout.TotalSeconds)}";

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    internal static void StopFailedSuccessor(int processId)
    {
        if (processId == Environment.ProcessId)
        {
            throw new InvalidOperationException("Refusing to stop the incumbent conductor process.");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // The exact successor pid is already gone.
        }
    }

    private static long GetConductEventCursor(ConductLoopHandoffOptions options)
    {
        var path = ConductEventsPath(options);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static bool HasLoopStartAfterCursor(ConductLoopHandoffOptions options, long cursor)
    {
        var path = ConductEventsPath(options);
        if (!File.Exists(path))
            return false;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (cursor >= 0 && cursor <= stream.Length)
            stream.Position = cursor;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Contains("LOOP_START", StringComparison.Ordinal);
    }

    private static string ConductEventsPath(ConductLoopHandoffOptions options) =>
        Path.Combine(options.LogDirectory, ConductEventLogWriter.CurrentFileName);

    private static string ToLowerInvariant(bool value) =>
        value ? "true" : "false";

    private static string QuoteCommandArgument(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            var quoted = new StringBuilder();
            quoted.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', (backslashes * 2) + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    quoted.Append('\\', backslashes);
                    backslashes = 0;
                }

                quoted.Append(character);
            }

            if (backslashes > 0)
                quoted.Append('\\', backslashes * 2);

            quoted.Append('"');
            return quoted.ToString();
        }

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static void TryRecordHandoffEvent(string runEventStorePath, string status, string detail)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                eventName = "LOOP_HANDOFF",
                status,
                detail
            });
            new SqliteRunEventStore(runEventStorePath).AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorTick,
                GoalId: null,
                Operation: "LOOP_HANDOFF",
                Status: status,
                Detail: detail,
                PayloadJson: payload,
                OccurredAt: DateTimeOffset.UtcNow))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            EmitJournalFailure(runEventStorePath, status, ex);
        }
    }

    private static void EmitJournalFailure(string runEventStorePath, string status, Exception ex)
    {
        var line =
            $"LOOP_HANDOFF_JOURNAL_FAILED status={status} store={runEventStorePath} error={ex.GetType().Name}:{ex.Message}";
        Console.WriteLine(line);
        Console.Error.WriteLine(line);
        Console.Out.Flush();
        Console.Error.Flush();
    }

    private static void EmitHandoffFailure(string detail)
    {
        var line = $"LOOP_HANDOFF_FAILED {detail}";
        Console.WriteLine(line);
        Console.Error.WriteLine(line);
        Console.Out.Flush();
        Console.Error.Flush();
    }

    private static void EmitHandoffPending(ConductLoopHandoffOptions options, string detail)
    {
        var line = $"LOOP_HANDOFF_PENDING {detail}";
        Console.WriteLine(line);
        Console.Out.Flush();
        TryRecordHandoffEvent(options.RunEventStorePath, "Pending", detail);
        TryAppendConductEvent(options, line);
    }

    private static void TryAppendConductEvent(ConductLoopHandoffOptions options, string line)
    {
        try
        {
            new ConductEventLogWriter(ConductEventsPath(options)).Append("loop-handoff", null, line);
        }
        catch
        {
            // Handoff pending progress is advisory; stdout and run events remain available.
        }
    }

    private static string ToSafeName(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var safe = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "conduct-loop" : safe;
    }

    [Flags]
    private enum WindowsCreationFlags : uint
    {
        CreateNewProcessGroup = 0x00000200,
        CreateUnicodeEnvironment = 0x00000400,
        CreateBreakawayFromJob = 0x01000000,
        ExtendedStartupInfoPresent = 0x00080000
    }

    private const int ProcThreadAttributeHandleList = 0x00020002;

    [Flags]
    private enum WindowsStartupInfoFlags : int
    {
        UseStdHandles = 0x00000100
    }

    [Flags]
    private enum WindowsFileAccess : uint
    {
        GenericWrite = 0x40000000
    }

    [Flags]
    private enum WindowsFileShare : uint
    {
        Read = 0x00000001,
        Delete = 0x00000004
    }

    private enum WindowsCreationDisposition : uint
    {
        CreateAlways = 2
    }

    [Flags]
    private enum WindowsFileFlags : uint
    {
        FileAttributeNormal = 0x00000080
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsSecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsProcessStartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsProcessStartupInfoEx
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
        public IntPtr lpAttributeList;
    }

    private sealed class WindowsInheritedHandleList : IDisposable
    {
        public WindowsInheritedHandleList(IntPtr attributeList, IntPtr handleList, bool initialized)
        {
            AttributeList = attributeList;
            HandleList = handleList;
            Initialized = initialized;
        }

        public IntPtr AttributeList { get; }
        private IntPtr HandleList { get; }
        private bool Initialized { get; }

        public void Dispose()
        {
            if (Initialized)
                DeleteProcThreadAttributeList(AttributeList);
            Marshal.FreeHGlobal(HandleList);
            Marshal.FreeHGlobal(AttributeList);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        WindowsCreationFlags dwCreationFlags,
        string? lpEnvironment,
        string? lpCurrentDirectory,
        ref WindowsProcessStartupInfoEx lpStartupInfo,
        out WindowsProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(
        IntPtr processHandle,
        IntPtr jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        WindowsFileAccess dwDesiredAccess,
        WindowsFileShare dwShareMode,
        ref WindowsSecurityAttributes lpSecurityAttributes,
        WindowsCreationDisposition dwCreationDisposition,
        WindowsFileFlags dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
