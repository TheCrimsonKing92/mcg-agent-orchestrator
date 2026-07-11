using System.Diagnostics;
using System.Globalization;
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

internal static partial class ConductorLoopHandoff
{
    public const string RenewalCountFlag = "--handoff-renewals";
    public const int DefaultMaxRenewalsWithoutLanding = 6;
    private const string BatchNameEnvironmentVariable = "MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME";
    private static readonly Regex TimestampedOperatorLogName = new(
        @"^operator-(?<name>.+)-\d{14}\.(out|err)\.log$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static Func<ConductorLoopHandoffRequest, ConductorLoopHandoffResult> Create(
        ConductLoopHandoffOptions options,
        Func<ConductLoopLaunchRequest, ConductLoopLaunchResult>? launch = null) =>
        request => TryStartSuccessor(options, request, launch ?? LaunchDetached);

    internal static ConductorLoopHandoffResult TryStartSuccessor(
        ConductLoopHandoffOptions options,
        ConductorLoopHandoffRequest request,
        Func<ConductLoopLaunchRequest, ConductLoopLaunchResult> launch)
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
        var result = launch(launchRequest);
        TryRecordHandoffEvent(options.RunEventStorePath, "Started",
            $"pid={result.ProcessId} stdout={result.StdoutPath} stderr={result.StderrPath}");
        return ConductorLoopHandoffResult.StartedProcess(result.ProcessId, result.StdoutPath, result.StderrPath);
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
        var commandLine = string.Join(" ", command.Select(QuoteCommandArgument)) +
            " 1>" + QuoteCommandArgument(request.StdoutPath) +
            " 2>" + QuoteCommandArgument(request.StderrPath);
        var shell = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "/bin/sh";
        var shellArgs = OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", commandLine }
            : new[] { "-c", commandLine };

        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory
        };

        foreach (var arg in shellArgs)
            startInfo.ArgumentList.Add(arg);

        startInfo.Environment["MCG_ORCHESTRATOR_STDOUT_LOG_PATH"] = request.StdoutPath;
        startInfo.Environment["MCG_ORCHESTRATOR_STDERR_LOG_PATH"] = request.StderrPath;
        startInfo.Environment[BatchNameEnvironmentVariable] = request.Name;

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start conduct loop successor.");
        return new ConductLoopLaunchResult(process.Id, request.StdoutPath, request.StderrPath);
    }

    private static string QuoteCommandArgument(string value)
    {
        if (OperatingSystem.IsWindows())
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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

    private static string ToSafeName(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var safe = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "conduct-loop" : safe;
    }
}
