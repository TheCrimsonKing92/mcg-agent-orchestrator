using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Observations of transport work, never inputs to lane acceptance or retirement.
internal sealed record RemoteLaneStep(string Name, int? ExitCode, bool? TimedOut,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, string StderrTail,
    string? StdoutPath = null, string? StderrPath = null);
internal sealed record RemoteLanePollSummary(int PollCount, int FailedPollCount, string? LastState,
    DateTimeOffset? LastHeartbeatChangeAt, RemoteLaneStep? LastFailedPoll);
internal sealed record RemoteLaneAttemptDetail(IReadOnlyList<RemoteLaneStep> Steps,
    RemoteLanePollSummary? Poll, IReadOnlyList<RemoteLaneStep> Fetches, JsonElement? LastStatus,
    string? RunnerLogPath, string? ExceptionMessage,
    RemoteLaneLeaseDecision? LeaseDecision = null, RemoteLaneRunnerLogCaptureTiming? RunnerLogCaptureTiming = null);
// The predicate's operands are authoritative; the separately sampled diagnostics may be newer.
internal sealed record RemoteLaneLeaseDecision(DateTimeOffset ObservedAt, DateTimeOffset SubmittedAt,
    DateTimeOffset EffectiveHeartbeat, TimeSpan HeartbeatAge, int LeaseSeconds,
    string ExecutorId, string? RunnerAlias, string Lane, string GateAttemptId,
    RemoteLaneHandleDiagnostics? Snapshot);
internal sealed record RemoteLaneRunnerLogCaptureTiming(DateTimeOffset StartedAt, DateTimeOffset EndedAt);
internal sealed record RemoteLaneHandleDiagnostics(RemoteLanePollSummary Poll,
    IReadOnlyList<RemoteLaneStep> Fetches, JsonElement? LastStatus);
internal sealed record RemoteLaneRunnerLogCapture(string? Path, RemoteLaneStep? FailedStep = null);
internal interface IRemoteLaneAttemptDiagnosticsSource
{
    RemoteLaneHandleDiagnostics Snapshot();
    Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken cancellationToken);
}

internal static class RemoteLaneDiagnosticFiles
{
    internal const int MaxBytes = 32 * 1024;
    internal static RemoteLaneStep Step(string staging, string name, int? exitCode, bool? timedOut,
        DateTimeOffset startedAt, DateTimeOffset endedAt, string output, string? error, bool failed = false)
    {
        var tail = string.IsNullOrEmpty(error) ? output : error;
        return new(name, exitCode, timedOut, startedAt, endedAt, tail.Length > 2048 ? tail[^2048..] : tail,
            failed || exitCode is not null and not 0 || timedOut == true ? WriteTail(staging, name + ".out.txt", output) : null,
            failed || exitCode is not null and not 0 || timedOut == true ? WriteTail(staging, name + ".err.txt", error ?? "") : null);
    }

    internal static string? WriteTail(string staging, string name, string value)
    {
        try
        {
            // UTF-8 may use four bytes per character. Bound before encoding and retain whole characters.
            if (value.Length > MaxBytes) value = value[^MaxBytes..];
            var bytes = Encoding.UTF8.GetBytes(value);
            var offset = Math.Max(0, bytes.Length - MaxBytes);
            while (offset < bytes.Length && (bytes[offset] & 0xc0) == 0x80) offset++;
            var path = System.IO.Path.Combine(staging, name);
            File.WriteAllBytes(path, bytes[offset..]);
            return path;
        }
        catch (Exception) { return null; }
    }
}
