using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Simulation;

internal static class GateSchedulingSimulator
{
    internal static IReadOnlyList<RecordedGateMember> ComposeTrain(
        IReadOnlyList<RecordedGateMember> orderedMembers, int trainSize)
    {
        if (trainSize == 0) return [];
        var selected = new List<RecordedGateMember>();
        foreach (var member in orderedMembers)
        {
            if (selected.Any(other => Overlaps(member, other))) continue;
            selected.Add(member);
            if (selected.Count == trainSize) return selected.ToArray();
        }
        return [];
    }

    private static bool Overlaps(RecordedGateMember a, RecordedGateMember b) =>
        a.ResourceKeys.Intersect(b.ResourceKeys, StringComparer.OrdinalIgnoreCase).Any() ||
        a.LandingPaths.Any(p => b.LandingPaths.Any(q => RepositoryPathOverlap.Overlaps(p, q)));

    internal static BoardSimulationResult Simulate(BoardHistoryTrace trace, GateSchedulingPolicy policy)
    {
        policy.Validate();
        if (trace.WindowEnd <= trace.WindowStart) throw new ArgumentException("The trace window must be positive.");
        if (trace.GateRuns.Any(g => g.EndAt < g.BeginAt || g.Members.Count == 0))
            throw new ArgumentException("Invalid recorded gate.");
        var random = new Random(policy.Seed);
        var gates = new List<SimulatedGate>();
        var landings = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var waiting = new Dictionary<string, RecordedReadinessEpoch>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<int>();
        var active = new List<(SimulatedGate Gate, int? Record, bool Retried, RecordedReadinessEpoch[] Epochs)>();
        var epochs = trace.Epochs.OrderBy(e => e.ReadyAt).ThenBy(e => e.GoalId, StringComparer.Ordinal).ToArray();
        var priority = epochs.GroupBy(e => e.GoalId).ToDictionary(g => g.Key, g => g.Min(e => e.ReadyAt), StringComparer.Ordinal);
        var arrivals = new HashSet<int>();
        var leaves = new HashSet<int>();
        var now = trace.WindowStart;

        double Rate(RecordedGateMember member)
        {
            var decided = trace.GateRuns.Where(g => g.Outcome != GateRunOutcome.Infrastructure).ToArray();
            var own = decided.Where(g => g.Members.Any(m => m.GoalId == member.GoalId &&
                m.CandidateRevision == member.CandidateRevision)).ToArray();
            var sample = own.Length == 0 ? decided : own;
            return sample.Length == 0 ? 0 : sample.Count(g => g.Outcome == GateRunOutcome.Passed) / (double)sample.Length;
        }

        TimeSpan? LandingDelay(int index, string goal)
        {
            var record = trace.GateRuns[index];
            if (record.Outcome != GateRunOutcome.Passed) return null;
            var next = trace.GateRuns.Where(g => g.BeginAt > record.BeginAt && g.Members.Any(m => m.GoalId == goal))
                .Select(g => g.BeginAt).Concat(trace.Epochs.Where(e => e.GoalId == goal && e.LeaveAt > record.BeginAt)
                    .Select(e => e.LeaveAt!.Value)).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
            var landing = trace.Landings.FirstOrDefault(l => l.GoalId == goal && l.LandedAt > record.EndAt && l.LandedAt < next);
            return landing is null ? null : landing.LandedAt - record.EndAt;
        }

        var medianDelay = BoardSimulationCalibration.Median(trace.GateRuns.SelectMany((g, i) =>
            g.Members.Select(m => LandingDelay(i, m.GoalId)).Where(d => d.HasValue).Select(d => d!.Value.TotalMinutes)));

        RecordedGateMember Worst(IReadOnlyList<RecordedGateMember> members) =>
            members.Select((m, i) => (Member: m, Index: i)).OrderBy(x => Rate(x.Member))
                .ThenByDescending(x => x.Index).First().Member;

        void Start(GateRunKind kind, RecordedReadinessEpoch[] entries, bool retried = false,
            int? carriedRecord = null)
        {
            var members = entries.Select(e => e.Member).ToArray();
            var match = carriedRecord ?? trace.GateRuns.Select((g, i) => (Gate: g, Index: i))
                .Where(x => !used.Contains(x.Index) && x.Gate.Members.Count == members.Length &&
                    x.Gate.Members.All(m => members.Any(n => n.GoalId == m.GoalId && n.CandidateRevision == m.CandidateRevision)))
                .OrderBy(x => Math.Abs((x.Gate.BeginAt - now).Ticks)).ThenBy(x => x.Gate.BeginAt)
                .ThenBy(x => x.Index).Select(x => (int?)x.Index).FirstOrDefault();
            SimulatedGate gate;
            if (match is { } index)
            {
                used.Add(index);
                var record = trace.GateRuns[index];
                gate = new(kind, members, carriedRecord.HasValue ? record.BeginAt : now,
                    carriedRecord.HasValue ? record.EndAt : now + (record.EndAt - record.BeginAt),
                    record.Outcome, carriedRecord.HasValue ? SimulatedGateSource.CarriedIn : SimulatedGateSource.Recorded);
            }
            else
            {
                var durations = trace.GateRuns.Where(g => g.Kind == kind).Select(g => (g.EndAt - g.BeginAt).TotalMinutes).ToArray();
                if (durations.Length == 0) durations = trace.GateRuns.Select(g => (g.EndAt - g.BeginAt).TotalMinutes).ToArray();
                // Materialize every draw: a failed member must not short-circuit the seeded sequence.
                var draws = members.Select(m => random.NextDouble() < Rate(m)).ToArray();
                gate = new(kind, members, now, now.AddMinutes(BoardSimulationCalibration.Median(durations)),
                    draws.All(p => p) ? GateRunOutcome.Passed : GateRunOutcome.Failed, SimulatedGateSource.Sampled);
            }
            foreach (var entry in entries)
            {
                if (waiting.TryGetValue(entry.GoalId, out var queued) && queued == entry) waiting.Remove(entry.GoalId);
                busy.Add(entry.GoalId);
            }
            gates.Add(gate);
            active.Add((gate, match, retried, entries));
        }

        void Return(RecordedReadinessEpoch entry)
        {
            if (!landings.ContainsKey(entry.GoalId)) waiting.TryAdd(entry.GoalId, entry);
        }

        // Consume the readiness entries already represented by a gate crossing the window boundary.
        foreach (var item in trace.GateRuns.Select((g, i) => (Gate: g, Index: i))
                     .Where(x => x.Gate.BeginAt < now && x.Gate.EndAt >= now)
                     .OrderBy(x => x.Gate.BeginAt).ThenBy(x => x.Gate.Members[0].GoalId, StringComparer.Ordinal))
        {
            var entries = item.Gate.Members.Select(m =>
                epochs.LastOrDefault(e => e.GoalId == m.GoalId && e.ReadyAt <= now) ??
                new(m.GoalId, item.Gate.BeginAt, null, m.CandidateRevision, m.LandingPaths, m.ResourceKeys)).ToArray();
            for (var i = 0; i < epochs.Length; i++)
                if (entries.Any(e => e.GoalId == epochs[i].GoalId && epochs[i].ReadyAt <= e.ReadyAt)) arrivals.Add(i);
            // Preserve the recorded members even if the most recent epoch has a different revision.
            entries = entries.Select((e, i) => e with { CandidateRevision = item.Gate.Members[i].CandidateRevision,
                LandingPaths = item.Gate.Members[i].LandingPaths, ResourceKeys = item.Gate.Members[i].ResourceKeys }).ToArray();
            Start(item.Gate.Kind, entries, carriedRecord: item.Index);
        }

        while (now < trace.WindowEnd)
        {
            // Process newly created zero-duration retries at this instant before leave/arrival events.
            while (active.Any(a => a.Gate.EndAt <= now))
            {
                var ending = active.Where(a => a.Gate.EndAt <= now).OrderBy(a => a.Gate.EndAt)
                    .ThenBy(a => a.Gate.Members.Select(m => m.GoalId).Order(StringComparer.Ordinal).First(), StringComparer.Ordinal).First();
                active.Remove(ending);
                foreach (var entry in ending.Epochs) busy.Remove(entry.GoalId);
                if (ending.Gate.Outcome == GateRunOutcome.Passed)
                {
                    foreach (var entry in ending.Epochs)
                    {
                        var delay = ending.Record is { } index ? LandingDelay(index, entry.GoalId) : TimeSpan.FromMinutes(medianDelay);
                        if (delay is { } value)
                        {
                            landings.TryAdd(entry.GoalId, now + value);
                            waiting.Remove(entry.GoalId);
                        }
                        else Return(entry);
                    }
                }
                else if (ending.Gate.Outcome == GateRunOutcome.Infrastructure)
                    foreach (var entry in ending.Epochs) Return(entry);
                else if (ending.Gate.Kind == GateRunKind.Train && ending.Epochs.Length >= 3 && !ending.Retried)
                {
                    var drop = policy.DropRule == GateDropRule.Newest ? ending.Gate.Members[^1] : Worst(ending.Gate.Members);
                    Return(ending.Epochs.Single(e => e.GoalId == drop.GoalId));
                    Start(GateRunKind.Train, ending.Epochs.Where(e => e.GoalId != drop.GoalId).ToArray(), retried: true);
                }
                else
                {
                    var drop = Worst(ending.Gate.Members);
                    foreach (var entry in ending.Epochs.Where(e => e.GoalId != drop.GoalId)) Return(entry);
                }
            }
            for (var i = 0; i < epochs.Length; i++)
                if (!leaves.Contains(i) && epochs[i].LeaveAt <= now)
                {
                    leaves.Add(i);
                    if (waiting.TryGetValue(epochs[i].GoalId, out var entry) && entry == epochs[i]) waiting.Remove(entry.GoalId);
                }
            for (var i = 0; i < epochs.Length; i++)
                if (!arrivals.Contains(i) && epochs[i].ReadyAt <= now)
                {
                    arrivals.Add(i);
                    if (!landings.ContainsKey(epochs[i].GoalId) && !(epochs[i].LeaveAt <= now)) waiting[epochs[i].GoalId] = epochs[i];
                }
            while (active.Count < policy.Width)
            {
                var ordered = waiting.Values.Where(e => !busy.Contains(e.GoalId))
                    .OrderBy(e => priority.GetValueOrDefault(e.GoalId, e.ReadyAt)).ThenBy(e => e.GoalId, StringComparer.Ordinal).ToArray();
                if (ordered.Length == 0) break;
                var members = ordered.Select(e => e.Member).ToArray();
                var chosen = ComposeTrain(members, policy.TrainSize);
                var kind = GateRunKind.Train;
                if (chosen.Count == 0 && policy.CohortsEnabled) { chosen = ComposeTrain(members, 2); kind = GateRunKind.Cohort; }
                if (chosen.Count == 0) { chosen = [members[0]]; kind = GateRunKind.Solo; }
                Start(kind, chosen.Select(m => waiting[m.GoalId]).ToArray());
                if (active.Any(a => a.Gate.EndAt == now)) break;
            }
            var next = active.Select(a => a.Gate.EndAt)
                .Concat(epochs.Select((e, i) => (e, i)).Where(x => !arrivals.Contains(x.i)).Select(x => x.e.ReadyAt))
                .Concat(epochs.Select((e, i) => (e, i)).Where(x => !leaves.Contains(x.i) && x.e.LeaveAt.HasValue)
                    .Select(x => x.e.LeaveAt!.Value)).DefaultIfEmpty(trace.WindowEnd).Min();
            if (next < now) throw new InvalidOperationException("Simulation event time moved backwards.");
            now = next;
        }
        return new(gates.ToArray(), landings.Where(l => l.Value >= trace.WindowStart && l.Value < trace.WindowEnd)
            .Select(l => new SimulatedLanding(l.Key, l.Value)).OrderBy(l => l.LandedAt)
            .ThenBy(l => l.GoalId, StringComparer.Ordinal).ToArray(), gates.Sum(g => (g.EndAt - g.BeginAt).TotalMinutes));
    }
}
