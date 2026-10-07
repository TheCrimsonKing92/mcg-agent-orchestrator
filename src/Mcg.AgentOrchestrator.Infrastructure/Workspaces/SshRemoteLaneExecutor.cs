using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    internal const int RepresentativeAttemptFolderLength = 117;
    internal const int StagingPathBudget = 240;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pushGates = new(StringComparer.Ordinal);

    internal async Task<IDisposable> EnterPushGateAsync(string executorId, CancellationToken cancellationToken = default)
    {
        var gate = _pushGates.GetOrAdd(executorId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new PushGateLease(gate);
    }

    private sealed class PushGateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal static string SshPath => OpenSshPath("ssh.exe");
    internal static string ScpPath => OpenSshPath("scp.exe");
    private static string OpenSshPath(string name) => Path.Combine(
        Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "OpenSSH", name);
    internal static string LaneKey(string lane) => Regex.Replace(lane, "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();

    internal static string StagingFolderName(string executorId, string laneKey, int probe)
    {
        var identity = executorId + "\n" + laneKey;
        if (probe > 0) identity += "\n" + probe.ToString(CultureInfo.InvariantCulture);
        return "r-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..8];
    }

    internal static string PrepareStaging(string attemptFolder, string executorId, string lane, string laneKey)
    {
        for (var probe = 0; ; probe = checked(probe + 1))
        {
            var folder = Path.GetFullPath(Path.Combine(attemptFolder, StagingFolderName(executorId, laneKey, probe)));
            var manifest = Path.Combine(folder, "lane.json");
            if (Directory.Exists(folder))
            {
                if (MatchesLane(manifest)) return folder;
                continue;
            }

            Directory.CreateDirectory(folder);
            FileStream claim;
            try { claim = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(manifest))
            {
                if (MatchesLane(manifest)) return folder;
                continue;
            }
            using (claim) JsonSerializer.Serialize(claim, new { executorId, lane, laneKey });
            return folder;
        }

        bool MatchesLane(string manifest)
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(manifest));
                var root = json.RootElement;
                return root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("executorId", out var id) && id.ValueKind == JsonValueKind.String &&
                    root.TryGetProperty("lane", out var name) && name.ValueKind == JsonValueKind.String &&
                    root.TryGetProperty("laneKey", out var key) && key.ValueKind == JsonValueKind.String &&
                    string.Equals(id.GetString(), executorId, StringComparison.Ordinal) &&
                    string.Equals(key.GetString(), laneKey, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }

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
        var staging = PrepareStaging(Path.GetDirectoryName(Path.GetFullPath(attemptPrefix))!, entry.Id, request.Lane, laneKey);
        var steps = new List<RemoteLaneStep>();
        var startedAt = clock.GetUtcNow();
        GitCli.GitResult push;
        using (await EnterPushGateAsync(entry.Id, cancellationToken).ConfigureAwait(false))
        {
            push = await Task.Run(() => git(worktreePath, 600_000,
                ["-c", $"core.sshCommand={SshPath.Replace('\\', '/')}", "push",
                    $"{entry.RunnerAlias}:{entry.RemoteRepository}", $"{request.VerifyingCommitSha}:refs/heads/c-{shortSha}"]),
                cancellationToken).ConfigureAwait(false);
        }
        steps.Add(RemoteLaneDiagnosticFiles.Step(staging, "push", push.ExitCode, null,
            startedAt, clock.GetUtcNow(), push.Output, push.Error));
        if (push.ExitCode != 0) return new(null, "push-failed", steps.ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        var jobName = $"{request.AttemptId}-{laneKey}.json";
        await File.WriteAllTextAsync(Path.Combine(staging, "job.json"), JsonSerializer.Serialize(new
        {
            sha = request.VerifyingCommitSha, project = request.Project, filter = request.Filter, lane = request.Lane,
            attemptId = request.AttemptId, executorId = request.ExecutorId, filterHash = request.FilterHash,
            mainSha = request.MainSha, manifestIdentity = request.ManifestIdentity
        }), cancellationToken).ConfigureAwait(false);
        startedAt = clock.GetUtcNow();
        var copy = await transport([ScpPath, "-o", "BatchMode=yes", "job.json",
            $"{entry.RunnerAlias}:{entry.RunRoot}/queue/{jobName}"], staging, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        steps.Add(RemoteLaneDiagnosticFiles.Step(staging, "job-copy", copy.ExitCode, copy.TimedOut,
            startedAt, clock.GetUtcNow(), copy.Output, copy.Stderr));
        if (copy.ExitCode != 0 || copy.TimedOut) return new(null, "job-copy-failed", steps.ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        startedAt = clock.GetUtcNow();
        var trigger = await transport([SshPath, "-o", "BatchMode=yes", entry.AdminAlias!,
            "schtasks", "/run", "/tn", "mcg-executor-lane"], staging, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        steps.Add(RemoteLaneDiagnosticFiles.Step(staging, "trigger", trigger.ExitCode, trigger.TimedOut,
            startedAt, clock.GetUtcNow(), trigger.Output, trigger.Stderr));
        if (trigger.ExitCode != 0 || trigger.TimedOut) return new(null, "trigger-failed", steps.ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        return new(new SshRemoteLaneHandle(request, entry, staging, shortSha, laneKey, clock, transport,
            pollInterval ?? TimeSpan.FromSeconds(entry.PollSeconds), onPollCompleted), Steps: steps.ToArray());
    }
}

internal sealed record SshPollObservation(int PollIndex, string DestinationFolder);
