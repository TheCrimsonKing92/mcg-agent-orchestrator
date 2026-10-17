using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorInvariantObservationReader(string conductEventsLogPath,
    string groupedGateAttemptRoot, Func<DateTimeOffset, IReadOnlyList<GateHeartbeatStatus>> heartbeats)
{
    internal static ConductorInvariantObservationReader ForWorkspace(OrchestratorWorkspace workspace) =>
        new(workspace.ConductEventsLogPath, Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            now => GateHeartbeatArtifacts.ReadStableSlots(DotnetBuildEnvironmentManager.CaptureStorageRoot(), now));

    internal ConductorInvariantObservation Read(IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, DateTimeOffset now)
    {
        var ready = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var refusals = new List<ConductorInvariantRefusal>();
        foreach (var path in LogPaths())
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("GOAL goal=", StringComparison.Ordinal) &&
                    !line.Contains("ACCEPTANCE_COHORT_ADOPTION_REFUSED ", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var row = document.RootElement;
                    var detail = row.GetProperty("detail").GetString() ?? "";
                    var at = row.GetProperty("timestamp").GetDateTimeOffset();
                    if (at > now) continue;
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
        }

        DateTimeOffset? progress = null;
        void ObserveProgress(DateTimeOffset at)
        {
            if (at <= now && (progress is null || at > progress)) progress = at;
        }
        if (Directory.Exists(groupedGateAttemptRoot))
            foreach (var path in Directory.EnumerateFiles(groupedGateAttemptRoot, "*.attempt.json", SearchOption.AllDirectories))
            {
                try
                {
                    var attempt = ConductorGroupedGateAttemptCoordinator.Read(path);
                    if (attempt.ReconciledAt is { } at && attempt.Detail?.StartsWith("refused:", StringComparison.Ordinal) != true)
                        ObserveProgress(at);
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
        foreach (var heartbeat in heartbeats(now))
            if (heartbeat.IsAvailable && heartbeat.Snapshot is { } snapshot)
                ObserveProgress(snapshot.LastProgressAt);
        return new(now, ready.Select(pair => new ConductorInvariantReadyGoal(pair.Key, pair.Value))
            .OrderBy(goal => goal.ReadySince).ThenBy(goal => goal.GoalId, StringComparer.Ordinal).ToArray(),
            progress, refusals.OrderBy(refusal => refusal.At).ToArray());
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
