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

internal sealed record ConductLoopHandoffOptions(
    IReadOnlyList<string> Args,
    string ExecutionDirectory,
    string OrchestratorDirectory,
    string LogDirectory,
    string RunEventStorePath,
    string StopFilePath,
    int RenewalCount,
    int MaxRenewals,
    Action ReleaseCurrentLease);

internal sealed record ConductLoopLaunchRequest(
    string Name,
    IReadOnlyList<string> Args,
    string StdoutPath,
    string StderrPath,
    string WorkingDirectory);

internal sealed record ConductLoopLaunchResult(
    int ProcessId,
    string StdoutPath,
    string StderrPath);

internal sealed record ConductLoopHandoffVerification(
    bool ProcessAlive,
    bool StdoutLogExists,
    bool LoopStartJournaled,
    string Detail)
{
    public bool Succeeded => ProcessAlive && StdoutLogExists && LoopStartJournaled;
}

internal static partial class ConductorLoopHandoff
{
    public const string RenewalCountFlag = "--handoff-renewals";
    public const int DefaultMaxRenewalsWithoutLanding = 6;
    private const int MaxLaunchAttempts = 2;
    private static readonly TimeSpan DefaultVerificationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VerificationPollInterval = TimeSpan.FromMilliseconds(250);
    private const string BatchNameEnvironmentVariable = "MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME";
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
        var launchRequest = new ConductLoopLaunchRequest(successorName, args, stdoutPath, stderrPath, options.ExecutionDirectory);

        options.ReleaseCurrentLease();
        verify ??= (result, eventCursor) => VerifySuccessor(result, options, eventCursor);
        ConductLoopLaunchResult? lastLaunch = null;
        ConductLoopHandoffVerification? lastVerification = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxLaunchAttempts; attempt++)
        {
            var eventCursor = GetConductEventCursor(options);
            try
            {
                var result = launch(launchRequest);
                lastLaunch = result;
                var verification = verify(result, eventCursor);
                lastVerification = verification;
                var detail =
                    $"attempt={attempt} pid={result.ProcessId} stdout={result.StdoutPath} stderr={result.StderrPath} verification={verification.Detail}";

                if (verification.Succeeded)
                {
                    TryRecordHandoffEvent(options.RunEventStorePath, "Started", detail);
                    return ConductorLoopHandoffResult.StartedProcess(
                        result.ProcessId,
                        result.StdoutPath,
                        result.StderrPath,
                        verification.Detail);
                }

                TryRecordHandoffEvent(options.RunEventStorePath, "Failed", detail);
                EmitHandoffFailure(detail);
            }
            catch (Exception ex)
            {
                lastError = ex;
                var detail =
                    $"attempt={attempt} stdout={launchRequest.StdoutPath} stderr={launchRequest.StderrPath} error={ex.GetType().Name}:{ex.Message}";
                TryRecordHandoffEvent(options.RunEventStorePath, "Failed", detail);
                EmitHandoffFailure(detail);
            }
        }

        var failureReason = lastError is null
            ? "successor-verification-failed"
            : $"successor-launch-failed {lastError.GetType().Name}:{lastError.Message}";
        var verificationOutcome = lastVerification?.Detail ?? "not-verified";
        TryRecordHandoffEvent(options.RunEventStorePath, "Escalated",
            $"reason={failureReason} stdout={stdoutPath} stderr={stderrPath} verification={verificationOutcome}");
        return ConductorLoopHandoffResult.FailedStart(
            failureReason,
            stdoutPath,
            stderrPath,
            verificationOutcome,
            lastLaunch?.ProcessId);
    }

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

        result.Add(RenewalCountFlag);
        result.Add(renewalCount.ToString(CultureInfo.InvariantCulture));
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
        var commandLineArgs = Environment.GetCommandLineArgs();
        var executable = Environment.ProcessPath ?? "dotnet";
        var command = new List<string> { executable };

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Length > 0)
        {
            command.Add(commandLineArgs[0]);
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start conduct loop successor.");
        var pidText = process.StandardOutput.ReadLine();
        if (!process.WaitForExit(5000) || process.ExitCode != 0)
            throw new InvalidOperationException("Detached conduct loop launcher did not exit cleanly.");
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            throw new InvalidOperationException("Detached conduct loop launcher did not report a successor pid.");

        return new ConductLoopLaunchResult(pid, request.StdoutPath, request.StderrPath);
    }

    private static ConductLoopLaunchResult LaunchDetachedWindows(ConductLoopLaunchRequest request, IReadOnlyList<string> command)
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(cmd))
            cmd = "cmd.exe";

        var commandLine = new StringBuilder(BuildWindowsBreakawayCommandLine(cmd, command, request.StdoutPath, request.StderrPath));
        var environment = BuildWindowsEnvironmentBlock(request);
        var startupInfo = new WindowsProcessStartupInfo
        {
            cb = Marshal.SizeOf<WindowsProcessStartupInfo>()
        };

        if (!CreateProcessW(
                lpApplicationName: cmd,
                lpCommandLine: commandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: WindowsCreationFlags.CreateBreakawayFromJob |
                    WindowsCreationFlags.CreateNewProcessGroup |
                    WindowsCreationFlags.CreateNoWindow |
                    WindowsCreationFlags.DetachedProcess |
                    WindowsCreationFlags.CreateUnicodeEnvironment,
                lpEnvironment: environment,
                lpCurrentDirectory: request.WorkingDirectory,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to start breakaway conduct loop successor.");
        }

        try
        {
            return new ConductLoopLaunchResult((int)processInformation.dwProcessId, request.StdoutPath, request.StderrPath);
        }
        finally
        {
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
        }
    }

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

    private static ConductLoopHandoffVerification VerifySuccessor(
        ConductLoopLaunchResult result,
        ConductLoopHandoffOptions options,
        long eventCursor)
    {
        var deadline = DateTimeOffset.UtcNow.Add(DefaultVerificationTimeout);
        var processAlive = false;
        var stdoutLogExists = false;
        var loopStartJournaled = false;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            processAlive = IsProcessAlive(result.ProcessId);
            stdoutLogExists = File.Exists(result.StdoutPath);
            loopStartJournaled = HasLoopStartAfterCursor(options, eventCursor);
            if (processAlive && stdoutLogExists && loopStartJournaled)
                break;

            Thread.Sleep(VerificationPollInterval);
        }

        var detail =
            $"processAlive={ToLowerInvariant(processAlive)} " +
            $"stdoutLogExists={ToLowerInvariant(stdoutLogExists)} " +
            $"loopStartJournaled={ToLowerInvariant(loopStartJournaled)}";
        return new ConductLoopHandoffVerification(processAlive, stdoutLogExists, loopStartJournaled, detail);
    }

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
        DetachedProcess = 0x00000008,
        CreateNoWindow = 0x08000000
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
        ref WindowsProcessStartupInfo lpStartupInfo,
        out WindowsProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
