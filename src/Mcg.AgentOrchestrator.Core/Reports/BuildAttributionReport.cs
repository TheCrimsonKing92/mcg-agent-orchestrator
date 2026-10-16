using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record BuildAttributionJournalRecord(string Event, DateTimeOffset RecordedAt,
    int ProcessId, string LeaseId, string Decision, string Reason, string TriggerPath);
public sealed record BuildAttributionRun(string Path, string GoalPrefix, string RunId, IReadOnlyList<string>? LogLines);
public sealed record BuildAttributionAttempt(string AttemptId, string? Head, DateTimeOffset? CompletedAt,
    string? Kind = null, string? FaultKind = null, bool PreReview = false);
public sealed record BuildAttributionInput(IReadOnlyList<BuildAttributionJournalRecord> Records,
    IReadOnlyList<BuildAttributionRun> Runs, IReadOnlyList<BuildAttributionAttempt> Attempts, int UnreadableRecords = 0);
public sealed record BuildAttributionSummary(int Holds, long HeldMs, int Compiles, double? OverallDuplicateFactor,
    int UnresolvedTreeHolds, int UnrecordedTreeHolds, int UnknownCompileHolds, int UnreadableRecords,
    string Cpu = "unrecorded");
public sealed record BuildAttributionConsumerRow(string Class, int Holds, long HeldMs, double HeldShare,
    int Compiles, int Waits, long WaitMs, int Timeouts);
public sealed record BuildAttributionTreeRow(string Tree, int Compiles, int DistinctProjects,
    double? DuplicateFactor, int DistinctHeads, long HeldMs, string ByClass);

/// <summary>Pure attribution and counting rules for the build-slot measurement sources.</summary>
public sealed record BuildAttributionReport(BuildAttributionSummary Summary,
    IReadOnlyList<BuildAttributionConsumerRow> Consumers, IReadOnlyList<BuildAttributionTreeRow> Trees)
{
    public static bool IsInWindow(DateTimeOffset at, DateTimeOffset since, DateTimeOffset until) =>
        at >= since && at < until;

    public static bool TryParseRunTime(string runId, out DateTimeOffset at) =>
        DateTimeOffset.TryParseExact(runId.Length >= 19 ? runId[..19] : runId, "yyyyMMdd'T'HHmmssfff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);

    public static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool ShouldReadRun(string path, string runId, IReadOnlyList<BuildAttributionJournalRecord> records,
        DateTimeOffset since, DateTimeOffset until) =>
        records.Any(r => r.Event == "LEASE_HELD" && IsInWindow(r.RecordedAt, since, until) &&
            SamePath(r.TriggerPath, path)) ||
        !TryParseRunTime(runId, out var at) || IsInWindow(at, since, until);

    public static BuildAttributionReport Build(BuildAttributionInput input, DateTimeOffset since,
        DateTimeOffset until, Func<string, string?> treeResolver)
    {
        var consumers = new Dictionary<string, Consumer>(StringComparer.Ordinal);
        var trees = new Dictionary<string, Tree>(StringComparer.Ordinal);
        var witnesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var attempts = input.Attempts.GroupBy(a => a.AttemptId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var runs = input.Runs.GroupBy(r => NormalizePath(r.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = input.UnreadableRecords;
        var unresolvedHolds = 0;
        var unrecordedHolds = 0;
        var unknownHolds = 0;

        Consumer ConsumerFor(string name)
        {
            if (!consumers.TryGetValue(name, out var row)) consumers.Add(name, row = new Consumer(name));
            return row;
        }
        Tree TreeFor(string name)
        {
            if (!trees.TryGetValue(name, out var row)) trees.Add(name, row = new Tree(name));
            return row;
        }
        void AddHold(string consumer, string tree, string? head, long heldMs,
            IReadOnlyList<(string Project, string Configuration)>? compiles)
        {
            var c = ConsumerFor(consumer);
            var t = TreeFor(tree);
            c.Holds++;
            c.HeldMs += heldMs;
            t.HeldMs += heldMs;
            if (tree == "unresolved") unresolvedHolds++;
            if (tree.StartsWith("unrecorded:", StringComparison.Ordinal)) unrecordedHolds++;
            if (head is not null && Regex.IsMatch(head, "\\A[0-9a-fA-F]{40}\\z")) t.Heads.Add(head);
            if (compiles is null) { unknownHolds++; return; }
            foreach (var pair in compiles)
            {
                c.Compiles++;
                t.Compiles++;
                t.Pairs.Add(pair.Project + "\0" + pair.Configuration);
                t.ByClass[consumer] = t.ByClass.GetValueOrDefault(consumer) + 1;
            }
        }

        // A pending file can be observed again after a concurrent drain into the journal.
        foreach (var record in input.Records.Distinct())
        {
            var details = Details(record.Reason);
            var holder = details.GetValueOrDefault("holder");
            var consumer = string.IsNullOrWhiteSpace(holder) || holder == "unlabeled"
                ? "unlabeled:" + record.LeaseId.Split('-')[0] : holder.Split(':')[0];
            if (record.Event == "LEASE_HELD" && consumer == "worker-build-check" && record.TriggerPath.Length > 0)
                referenced.Add(NormalizePath(record.TriggerPath));
            if (!IsInWindow(record.RecordedAt, since, until)) continue;
            if (record.Event == "ACCEPTANCE_SLOT_PRIORITY_WAIT")
            {
                var c = ConsumerFor("cohort-gate");
                c.Waits++;
                c.WaitMs += Milliseconds(details, "waitMs");
                if (record.Decision != "acquired") c.Timeouts++;
                continue;
            }
            if (record.Event is not ("LEASE_HELD" or "LEASE_WAIT_TIMEOUT")) continue;
            var waitMs = Milliseconds(details, "waitMs");
            if (waitMs > 0 || record.Event == "LEASE_WAIT_TIMEOUT")
            {
                var c = ConsumerFor(consumer);
                c.Waits++;
                c.WaitMs += waitMs;
                if (record.Event == "LEASE_WAIT_TIMEOUT") c.Timeouts++;
            }
            if (record.Event != "LEASE_HELD") continue;
            string tree = "unresolved";
            string? head = details.GetValueOrDefault("head");
            IReadOnlyList<(string, string)>? compiles = null;
            if (consumer == "worker-build-check")
            {
                var digest = details.GetValueOrDefault("tree");
                if (digest?.StartsWith("wbc1:git-tree:", StringComparison.Ordinal) == true && digest.Length > 14)
                    tree = digest[14..];
                if (record.TriggerPath.Length > 0 && runs.TryGetValue(NormalizePath(record.TriggerPath), out var run) &&
                    run.LogLines is not null)
                    compiles = CompileNames(run.LogLines).Select(p => (p, details.GetValueOrDefault("configuration") ?? "unrecorded")).ToArray();
            }
            else
            {
                var marker = holder?.LastIndexOf(":attempt-", StringComparison.Ordinal) ?? -1;
                if (marker >= 0 && attempts.TryGetValue(holder![(marker + 9)..], out var attempt))
                {
                    head = attempt.Head;
                    if (!string.IsNullOrWhiteSpace(head))
                    {
                        if (!resolved.TryGetValue(head, out tree!))
                        {
                            try { tree = treeResolver(head) ?? "unresolved"; }
                            catch (Exception) { tree = "unresolved"; }
                            if (string.IsNullOrWhiteSpace(tree)) tree = "unresolved";
                            resolved[head] = tree;
                        }
                    }
                }
                var compiled = details.GetValueOrDefault("compiled");
                if (compiled is not null && compiled != "unknown")
                {
                    var pairs = new List<(string, string)>();
                    if (compiled != "none")
                    foreach (var entry in compiled.Split('|'))
                    {
                        var slash = entry.IndexOf('/');
                        var at = entry.LastIndexOf('@');
                        if (slash <= 0 || at <= slash + 1 || !long.TryParse(entry[(at + 1)..],
                            NumberStyles.None, CultureInfo.InvariantCulture, out _)) { unreadable++; continue; }
                        var project = entry[..slash];
                        var configuration = entry[(slash + 1)..at];
                        if (string.IsNullOrWhiteSpace(record.TriggerPath)) { unreadable++; continue; }
                        var identity = NormalizePath(record.TriggerPath) + "\0" + project + "\0" + configuration + "\0" + entry[(at + 1)..];
                        if (witnesses.Add(identity)) pairs.Add((project, configuration));
                    }
                    compiles = pairs;
                }
            }
            AddHold(consumer, tree, head, Milliseconds(details, "heldMs"), compiles);
        }
        foreach (var run in runs.Values)
        {
            if (referenced.Contains(NormalizePath(run.Path))) continue;
            if (!TryParseRunTime(run.RunId, out var at)) { unreadable++; continue; }
            if (!IsInWindow(at, since, until)) continue;
            AddHold("worker-build-check", "unrecorded:goal-" + run.GoalPrefix, null,
                ElapsedMilliseconds(run.LogLines ?? []), run.LogLines is null ? null :
                    CompileNames(run.LogLines).Select(p => (p, "unrecorded")).ToArray());
        }
        foreach (var attempt in input.Attempts)
        {
            if (!attempt.PreReview || attempt.Kind != "fault" || attempt.FaultKind != "blocked-build-slot") continue;
            if (attempt.CompletedAt is null) { unreadable++; continue; }
            if (!IsInWindow(attempt.CompletedAt.Value, since, until)) continue;
            var c = ConsumerFor("pre-review-evidence");
            c.Waits++;
            c.Timeouts++;
        }
        var totalMs = consumers.Values.Sum(c => c.HeldMs);
        var knownTrees = trees.Values.Where(t => t.Name != "unresolved" && !t.Name.StartsWith("unrecorded:", StringComparison.Ordinal)).ToArray();
        return new(new(consumers.Values.Sum(c => c.Holds), totalMs, consumers.Values.Sum(c => c.Compiles),
                Factor(knownTrees.Sum(t => t.Compiles), knownTrees.Sum(t => t.Pairs.Count)),
                unresolvedHolds, unrecordedHolds, unknownHolds, unreadable),
            consumers.Values.OrderByDescending(c => c.HeldMs).ThenBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new BuildAttributionConsumerRow(c.Name, c.Holds, c.HeldMs,
                    totalMs == 0 ? 0 : Math.Round((double)c.HeldMs / totalMs, 3), c.Compiles, c.Waits, c.WaitMs, c.Timeouts)).ToArray(),
            trees.Values.Select(t => new BuildAttributionTreeRow(t.Name, t.Compiles, t.Pairs.Count,
                    Factor(t.Compiles, t.Pairs.Count), t.Heads.Count, t.HeldMs,
                    string.Join(';', t.ByClass.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.ToString(CultureInfo.InvariantCulture)}"))))
                .OrderByDescending(t => t.DuplicateFactor).ThenByDescending(t => t.Compiles).ThenBy(t => t.Tree, StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, string> Details(string reason) => reason.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0], StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);
    private static long Milliseconds(Dictionary<string, string> details, string key) =>
        long.TryParse(details.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 0;
    private static double? Factor(int count, int pairs) => pairs == 0 ? null : Math.Round((double)count / pairs, 3);
    private static IEnumerable<string> CompileNames(IReadOnlyList<string> lines) => lines
        .Select(l => Regex.Match(l, @"Compilation request (.+?) \("))
        .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);
    private static long ElapsedMilliseconds(IReadOnlyList<string> lines) => lines.Sum(l =>
    {
        var m = Regex.Match(l, @"Time Elapsed (\d+:\d+:\d+(?:\.\d+)?)");
        return m.Success && TimeSpan.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var span)
            ? (long)Math.Floor(span.TotalMilliseconds) : 0;
    });
    private static bool SamePath(string a, string b) => a.Length > 0 &&
        NormalizePath(a).Equals(NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class Consumer(string name)
    {
        public string Name { get; } = name;
        public int Holds, Compiles, Waits, Timeouts;
        public long HeldMs, WaitMs;
    }
    private sealed class Tree(string name)
    {
        public string Name { get; } = name;
        public int Compiles;
        public long HeldMs;
        public HashSet<string> Pairs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Heads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ByClass { get; } = new(StringComparer.Ordinal);
    }
}
