using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Transport produces evidence; the coordinator alone decides whether that evidence binds.
internal sealed class SshRemoteLaneExecutor(
    RemoteLaneExecutorConfiguration configuration, string worktreePath, string? attemptPrefix,
    TimeProvider clock,
    Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport,
    Func<string, int, string[], GitCli.GitResult> git,
    TimeSpan? pollInterval = null, Action<SshPollObservation>? onPollCompleted = null) : IRemoteLaneExecutor
{
    internal static string SshPath => OpenSshPath("ssh.exe");
    internal static string ScpPath => OpenSshPath("scp.exe");
    private static string OpenSshPath(string name) => Path.Combine(
        Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "OpenSSH", name);
    internal static string LaneKey(string lane) => Regex.Replace(lane, "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();

    public async Task<RemoteLaneSubmission> SubmitAsync(RemoteLaneRequest request, CancellationToken cancellationToken)
    {
        var entry = configuration.Executors.FirstOrDefault(entry => entry.Id == request.ExecutorId);
        if (entry?.Transport != "ssh") return new(null, "transport-unavailable");
        if (attemptPrefix is null) return new(null, "no-attempt-folder");
        var laneKey = LaneKey(request.Lane);
        if (!Regex.IsMatch(request.VerifyingCommitSha, "^[A-Za-z0-9-]{9,}\\z") ||
            !Regex.IsMatch(request.AttemptId, "^[A-Za-z0-9_-]+\\z") || laneKey.Length == 0 ||
            entry.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new(null, "invalid-request");
        var shortSha = request.VerifyingCommitSha[..9];
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(attemptPrefix))!,
            $"remote-{entry.Id}-{laneKey}");
        Directory.CreateDirectory(staging);
        var push = await Task.Run(() => git(worktreePath, 600_000,
            ["-c", $"core.sshCommand={SshPath.Replace('\\', '/')}", "push",
                $"{entry.RunnerAlias}:{entry.RemoteRepository}", $"{request.VerifyingCommitSha}:refs/heads/c-{shortSha}"]),
            cancellationToken).ConfigureAwait(false);
        if (push.ExitCode != 0) return new(null, "push-failed");
        cancellationToken.ThrowIfCancellationRequested();
        var jobName = $"{request.AttemptId}-{laneKey}.json";
        await File.WriteAllTextAsync(Path.Combine(staging, jobName), JsonSerializer.Serialize(new
        {
            sha = request.VerifyingCommitSha, project = request.Project, filter = request.Filter, lane = request.Lane,
            attemptId = request.AttemptId, executorId = request.ExecutorId, filterHash = request.FilterHash,
            mainSha = request.MainSha, manifestIdentity = request.ManifestIdentity
        }), cancellationToken).ConfigureAwait(false);
        var copy = await transport([ScpPath, "-o", "BatchMode=yes", jobName,
            $"{entry.RunnerAlias}:{entry.RunRoot}/queue/"], staging, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (copy.ExitCode != 0 || copy.TimedOut) return new(null, "job-copy-failed");
        cancellationToken.ThrowIfCancellationRequested();
        var trigger = await transport([SshPath, "-o", "BatchMode=yes", entry.AdminAlias!,
            "schtasks", "/run", "/tn", "mcg-executor-lane"], staging, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (trigger.ExitCode != 0 || trigger.TimedOut) return new(null, "trigger-failed");
        cancellationToken.ThrowIfCancellationRequested();
        return new(new SshRemoteLaneHandle(request, entry, staging, shortSha, laneKey, clock, transport,
            pollInterval ?? TimeSpan.FromSeconds(entry.PollSeconds), onPollCompleted));
    }
}

internal sealed record SshPollObservation(int PollIndex, string DestinationFolder);
