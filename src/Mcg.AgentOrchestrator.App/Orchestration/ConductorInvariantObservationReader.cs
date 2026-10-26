using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorInvariantObservationReader(string conductEventsLogPath,
    string groupedGateAttemptRoot, Func<DateTimeOffset, IReadOnlyList<GateHeartbeatStatus>> heartbeats,
    string? laneHeartbeatRoot = null, IReadOnlyList<string>? attemptRoots = null, bool incremental = true)
{
    private sealed record GroupedAttempt(long Length, DateTime LastWriteUtc, DateTimeOffset? ReconciledAt, bool Refused);
    private readonly Dictionary<string, GroupedAttempt> _groupedAttempts = new(StringComparer.Ordinal);
    internal ConductEventsLogTail LogTail { get; } = new();
    internal int LastGroupedAttemptReads { get; private set; }

    internal static ConductorInvariantObservationReader ForWorkspace(OrchestratorWorkspace workspace) =>
        ForWorkspace(workspace, DotnetBuildEnvironmentManager.CaptureStorageRoot());
    internal static ConductorInvariantObservationReader ForWorkspace(OrchestratorWorkspace workspace, DotnetBuildStorageRoot storageRoot) =>
        new(workspace.ConductEventsLogPath, Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            now => GateHeartbeatArtifacts.ReadStableSlots(storageRoot, now), Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            [Path.Combine(workspace.OrchestratorDirectory, "pre-review-evidence-attempts"),
             Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts")]);

    internal ConductorInvariantObservation Read(IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, DateTimeOffset now)
    {
        var ready = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var goalLines = new Dictionary<string, List<ConductorGoalLine>>(StringComparer.Ordinal);
        var refusals = new List<ConductorInvariantRefusal>();
        var relaunch = ConductorRelaunchObservation.Empty;
        DateTimeOffset? progress = null;
        void ObserveProgress(DateTimeOffset at)
        {
            if (at <= now && (progress is null || at > progress)) progress = at;
        }
        foreach (var (at, detail) in incremental ? LogTail.Rows(LogPaths().ToArray()) : FullLogRows())
        {
            try
            {
                if (at > now) continue;
                if (ConductorInvariantLogProgress.TryGetProgress(detail)) ObserveProgress(at);
                relaunch = ConductorRelaunchLogFold.Apply(relaunch, at, detail);
                var tokens = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string? Value(string name) => tokens.FirstOrDefault(token => token.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
                if (detail.StartsWith("ACCEPTANCE_COHORT_ADOPTION_REFUSED ", StringComparison.Ordinal))
                {
                    if (Value("members") is { Length: > 0 } members && Value("reason") is { } reason && Value("field") is { } field)
                        refusals.Add(new(members, reason + " field=" + field, at));
                }
                else if (detail.StartsWith("GOAL ", StringComparison.Ordinal) && Value("goal") is { } label)
                {
                    var matches = goals.Where(goal => goal.Id.Value.StartsWith(label, StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1) continue;
                    var goal = matches[0];
                    var reason = Value("reason") ?? "";
                    ConductorFinishedAttemptDiagnosis.Retain(goalLines, goal.Id.Value, new(at, Value("result") ?? "", reason, Value("decision")));
                    var capacity = reason.Contains("acceptance_width_", StringComparison.Ordinal) ||
                        reason.Contains("CohortCapacity", StringComparison.Ordinal) ||
                        reason.Contains("Acceptance_cohort_gate_owns_this_member", StringComparison.Ordinal);
                    if (Value("result") == "held" && capacity && goal.Status is not
                        (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
                        ready.TryAdd(goal.Id.Value, at); // Repeated holds do not restart the ready window.
                    else ready.Remove(goal.Id.Value);
                }
            }
            catch (JsonException) { } // A partially appended line is not evidence.
            catch (InvalidOperationException) { }
            catch (KeyNotFoundException) { }
            catch (FormatException) { }
        }

        LastGroupedAttemptReads = 0;
        var groupedPaths = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(groupedGateAttemptRoot))
            foreach (var path in Directory.EnumerateFiles(groupedGateAttemptRoot, "*.attempt.json", SearchOption.AllDirectories))
            {
                groupedPaths.Add(path);
                try
                {
                    long length = 0;
                    DateTime written = default;
                    if (incremental)
                    {
                        // Capture before deserializing so a subsequent replacement invalidates this entry.
                        var file = new FileInfo(path);
                        length = file.Length;
                        written = file.LastWriteTimeUtc;
                    }
                    if (!incremental || !_groupedAttempts.TryGetValue(path, out var cached) ||
                        cached.Length != length || cached.LastWriteUtc != written)
                    {
                        LastGroupedAttemptReads++;
                        var attempt = ConductorGroupedGateAttemptCoordinator.Read(path);
                        cached = new(length, written, attempt.ReconciledAt,
                            attempt.Detail?.StartsWith("refused:", StringComparison.Ordinal) == true);
                        if (incremental) _groupedAttempts[path] = cached;
                    }
                    if (cached.ReconciledAt is { } at && !cached.Refused)
                        ObserveProgress(at);
                }
                catch (IOException) { _groupedAttempts.Remove(path); }
                catch (JsonException) { _groupedAttempts.Remove(path); }
            }
        foreach (var path in _groupedAttempts.Keys.Where(path => !groupedPaths.Contains(path)).ToArray()) _groupedAttempts.Remove(path);
        foreach (var heartbeat in heartbeats(now))
            if (heartbeat.IsAvailable && heartbeat.Snapshot is { } snapshot)
                ObserveProgress(snapshot.LastProgressAt);
        if (laneHeartbeatRoot is not null)
        {
            var window = ConductorInvariantThresholds.GateIdleWhileReady;
            var directories = incremental ? ConductorAttemptDirectoryScope.HeartbeatDirectories(laneHeartbeatRoot, goals, now, window) : null;
            var lane = directories is null ? ConductorGateLaneProgressScanner.LatestProgress(laneHeartbeatRoot, now, window) :
                ConductorGateLaneProgressScanner.LatestProgress(laneHeartbeatRoot, now, window, directories);
            var remote = directories is null ? ConductorAttemptHeartbeatScanner.LatestProgress(laneHeartbeatRoot, now, window) :
                ConductorAttemptHeartbeatScanner.LatestProgress(laneHeartbeatRoot, now, window, directories);
            if (lane is { } laneAt) ObserveProgress(laneAt);
            if (remote is { } remoteAt) ObserveProgress(remoteAt);
        }
        return new(now, ready.Select(pair => new ConductorInvariantReadyGoal(pair.Key, pair.Value))
            .OrderBy(goal => goal.ReadySince).ThenBy(goal => goal.GoalId, StringComparer.Ordinal).ToArray(),
            progress, refusals.OrderBy(refusal => refusal.At).ToArray(),
            attemptRoots is null ? [] : incremental ? ConductorFinishedAttemptScanner.Scan(attemptRoots, goals, now,
                root => ConductorAttemptDirectoryScope.FinishedAttemptDirectories(root, goals), goalLines) :
                ConductorFinishedAttemptScanner.Scan(attemptRoots, goals, now, goalLines), relaunch);
    }

    private IEnumerable<(DateTimeOffset At, string Detail)> FullLogRows()
    {
        foreach (var path in LogPaths())
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (ConductEventsLogTail.ParseLine(line) is { } row) yield return row;
        }
    }

    private IEnumerable<string> LogPaths()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(conductEventsLogPath))!;
        if (!Directory.Exists(directory)) yield break;
        var stem = Path.GetFileNameWithoutExtension(conductEventsLogPath);
        var extension = Path.GetExtension(conductEventsLogPath);
        foreach (var path in Directory.EnumerateFiles(directory, stem + "-*" + extension)
            .OrderBy(path => path, StringComparer.Ordinal)) yield return path;
        if (File.Exists(conductEventsLogPath)) yield return conductEventsLogPath;
    }
}
