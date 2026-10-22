using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record DisjointLandingFactsRequest(
    string WorktreePath, string RecordTree, string RecordMain, string CurrentTree, string CurrentMain)
{
    internal Action CheckBudget { get; init; } = () => { };
}

internal sealed record DisjointLandingFacts(
    bool RecordMainIsAncestor, IReadOnlyList<string> LandedPaths,
    IReadOnlyList<string> RecordGoalDelta, IReadOnlyList<string> CurrentGoalDelta, IReadOnlyList<string> TreeDiff);

internal sealed record DisjointLandingDecision(string LaneId, string Decision, string ReasonCode, string SourceAttemptId);

// An attempt-local proof that only disjoint main landings changed a previously green lane's candidate.
internal static class AcceptanceDisjointLandingLaneReuse
{
    internal static DisjointLandingFacts? ResolveFactsFromGit(DisjointLandingFactsRequest request)
    {
        // Synthetic/unresolved identities must not cause process spawning in seam-only cache tests.
        if (new[] { request.RecordTree, request.RecordMain, request.CurrentTree, request.CurrentMain }
            .Any(token => token.Length is not (40 or 64) || !token.All(Uri.IsHexDigit))) return null;
        try
        {
            GitCli.GitResult Run(params string[] args)
            {
                request.CheckBudget();
                var result = GitCli.Run(request.WorktreePath, 5000, args);
                request.CheckBudget();
                if (!result.ProcessStarted || result.DrainTimedOut) throw new IOException("git facts unavailable");
                return result;
            }
            IReadOnlyList<string> Diff(string from, string to)
            {
                var result = Run("diff", "--name-only", "--no-renames", "-z", from, to, "--");
                if (!result.Succeeded) throw new IOException("git diff unavailable");
                return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToArray();
            }
            var ancestor = Run("merge-base", "--is-ancestor", request.RecordMain, request.CurrentMain);
            if (ancestor.ExitCode is not (0 or 1)) return null;
            return new(ancestor.ExitCode == 0, Diff(request.RecordMain, request.CurrentMain),
                Diff(request.RecordMain, request.RecordTree), Diff(request.CurrentMain, request.CurrentTree),
                Diff(request.RecordTree, request.CurrentTree));
        }
        catch { return null; }
    }

    internal sealed class Session(
        string worktreePath, string goalId, string attemptId, string currentTree, string currentMain,
        IReadOnlyList<PartitionVerdictRecord> records,
        Func<DisjointLandingFactsRequest, DisjointLandingFacts?>? resolveFacts = null,
        TimeProvider? timeProvider = null)
    {
        // Serialize decisions so the cumulative active budget and once-per-pair computation agree
        // even when remote early offers and local lane scheduling consult the cache concurrently.
        private readonly object _gate = new();
        private readonly Dictionary<(string Tree, string Main), PairOutcome> _pairs = [];
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
        private TimeSpan _spent;
        private long _started;
        private IReadOnlyList<AcceptanceTestClassSource>? _testSources;
        private Dictionary<string, AcceptanceLaneCarryShadowReach.Source>? _sourceIndex;

        internal (DisjointLandingDecision Decision, PartitionVerdictRecord? Record) Decide(
            string laneId, string filterHash, string laneFilter,
            Func<PartitionVerdictRecord, bool> keyMatches,
            Func<PartitionVerdictRecord, bool> hasStructuralEvidence)
        {
            lock (_gate)
            {
                _started = _time.GetTimestamp();
                PartitionVerdictRecord? source = null;
                (DisjointLandingDecision, PartitionVerdictRecord?) Decline(string reason) =>
                    (new(laneId, "declined", reason, source?.AttemptId ?? "none"), null);
                try
                {
                    CheckBudget();
                    var lastReason = "no-qualifying-record";
                    foreach (var record in records.Where(record =>
                                 record.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase) &&
                                 record.AttemptId != attemptId && record.PartitionId == laneId &&
                                 record.PartitionFilterHash == filterHash && record.Passed && record.Verdict == "GREEN")
                             .OrderByDescending(record => record.RecordedAt))
                    {
                        CheckBudget();
                        if (!keyMatches(record)) continue;
                        source = record;
                        if (records.Any(other => other.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase) &&
                                other.PartitionFilterHash == filterHash && !other.Passed &&
                                other.RecordedAt >= record.RecordedAt))
                        { lastReason = "newer-red-record"; continue; }
                        if (records.Where(other => other.AttemptId == record.AttemptId)
                            .Any(other => !other.Passed || other.Verdict != "GREEN"))
                        { lastReason = "attempt-not-all-green"; continue; }
                        if (record.MainSha == currentMain)
                        { lastReason = "landed-set-empty"; continue; }
                        var pairKey = (record.CandidateTreeSha, record.MainSha);
                        if (!_pairs.TryGetValue(pairKey, out var pair))
                        {
                            pair = PreparePair(record);
                            _pairs.Add(pairKey, pair);
                        }
                        CheckBudget();
                        if (pair.Changes is null) { lastReason = pair.Reason; continue; }
                        var classes = ReadTrxClasses(record.TestResultPaths);
                        CheckBudget();
                        if (classes.Reason is not null) { lastReason = classes.Reason; continue; }
                        _testSources ??= AcceptanceTestClassSourceScanner.ScanSources(worktreePath);
                        CheckBudget();
                        // A landing can introduce lane members absent from the prior TRX. Include
                        // current membership in the proof so those tests run against the goal delta.
                        var lane = new AcceptanceTestLane(laneId, laneFilter);
                        var membershipExpanded = _testSources.Any(item =>
                            !classes.Names.Contains(item.FullName, StringComparer.Ordinal) &&
                            item.SourcePaths.Any(pair.Changes.Paths.Contains) &&
                            AcceptanceLaneMembership.LanesIncluding([lane], item.FullName).Count > 0);
                        CheckBudget();
                        if (membershipExpanded) { lastReason = "lane-membership-expanded"; continue; }
                        var sources = classes.Names.Select(name =>
                            _testSources.FirstOrDefault(item => item.FullName == name)).ToArray();
                        if (sources.Any(item => item is null || item.SourcePaths.Count == 0))
                        { lastReason = "class-unresolved"; continue; }
                        var reach = AcceptanceLaneCarryShadowReach.Evaluate(pair.Changes,
                            sources.SelectMany(item => item!.SourcePaths).Distinct(StringComparer.Ordinal).ToArray(),
                            string.Join("\n", sources.Select(item => item!.SourceText)));
                        CheckBudget();
                        if (reach.Conservative) { lastReason = "lane-reached"; continue; }
                        if (!hasStructuralEvidence(record))
                        { lastReason = "missing-structural-coverage-evidence"; continue; }
                        CheckBudget();
                        return (new(laneId, "reused", "disjoint-unreached", record.AttemptId), record);
                    }
                    CheckBudget();
                    return Decline(lastReason);
                }
                catch (BudgetExceededException) { return Decline("budget-exceeded"); }
                catch { return Decline(IsOverBudget() ? "budget-exceeded" : "facts-exception"); }
                finally { _spent += _time.GetElapsedTime(_started); }
            }
        }

        private PairOutcome PreparePair(PartitionVerdictRecord record)
        {
            try
            {
                var facts = (resolveFacts ?? ResolveFactsFromGit)(new(worktreePath,
                    record.CandidateTreeSha, record.MainSha, currentTree, currentMain) { CheckBudget = CheckBudget });
                CheckBudget();
                if (facts is null) return new(null, "facts-unavailable");
                var landed = Paths(facts.LandedPaths);
                var goal = Paths(facts.RecordGoalDelta);
                if (!facts.RecordMainIsAncestor) return new(null, "main-not-ancestor");
                if (landed.Count == 0) return new(null, "landed-set-empty");
                if (!goal.SetEquals(Paths(facts.CurrentGoalDelta))) return new(null, "goal-delta-differs");
                if (landed.Overlaps(goal)) return new(null, "landed-overlaps-goal");
                if (!landed.SetEquals(Paths(facts.TreeDiff))) return new(null, "tree-diff-differs");
                if (landed.Any(AcceptanceLaneCarryShadowReach.IsSource))
                    _sourceIndex ??= BuildSourceIndex();
                CheckBudget();
                var changes = AcceptanceLaneCarryShadowReach.Prepare(landed.ToArray(),
                    _sourceIndex ?? new Dictionary<string, AcceptanceLaneCarryShadowReach.Source>(StringComparer.Ordinal));
                CheckBudget();
                return new(changes, "disjoint-unreached");
            }
            catch (BudgetExceededException) { throw; }
            catch { return new(null, "facts-exception"); }
        }

        private Dictionary<string, AcceptanceLaneCarryShadowReach.Source> BuildSourceIndex()
        {
            var result = new Dictionary<string, AcceptanceLaneCarryShadowReach.Source>(StringComparer.Ordinal);
            var root = Path.Combine(worktreePath, "src");
            if (!Directory.Exists(root)) return result;
            foreach (var path in EnumerateSourceFiles(root))
            {
                CheckBudget();
                result[Path.GetRelativePath(worktreePath, path).Replace('\\', '/')] =
                    AcceptanceLaneCarryShadowReach.IndexSource(File.ReadAllText(path));
            }
            CheckBudget();
            return result;
        }

        private bool IsOverBudget() => _spent + _time.GetElapsedTime(_started) > TimeSpan.FromSeconds(10);
        private void CheckBudget() { if (IsOverBudget()) throw new BudgetExceededException(); }
        private sealed class BudgetExceededException : Exception;
        private sealed record PairOutcome(AcceptanceLaneCarryShadowReach.ChangeSet? Changes, string Reason);
    }

    private static HashSet<string> Paths(IReadOnlyList<string> paths) =>
        paths.Select(path => string.IsNullOrWhiteSpace(path)
            ? throw new InvalidDataException("Missing changed path") : path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);

    private static (string[] Names, string? Reason) ReadTrxClasses(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return ([], "trx-no-class");
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            try
            {
                var document = XDocument.Load(path);
                var definitions = document.Descendants().Where(node => node.Name.LocalName == "UnitTest")
                    .Where(node => node.Attribute("id") is not null)
                    .GroupBy(node => node.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var tests = document.Descendants().Where(node => node.Name.LocalName == "UnitTestResult").ToArray();
                if (tests.Length == 0) return ([], "trx-no-class");
                foreach (var test in tests)
                {
                    if (AcceptanceTrxOutcomeTaxonomy.IsFatal(test.Attribute("outcome")?.Value)) return ([], "trx-not-green");
                    definitions.TryGetValue(test.Attribute("testId")?.Value ?? "", out var definition);
                    var name = definition?.Descendants().FirstOrDefault(node => node.Name.LocalName == "TestMethod")
                        ?.Attribute("className")?.Value;
                    if (string.IsNullOrWhiteSpace(name)) return ([], "class-unresolved");
                    classes.Add(name);
                }
            }
            catch { return ([], "trx-unreadable"); }
        }
        return (classes.ToArray(), null);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs")) yield return file;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory) is "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or
                "TestResults" or "playwright-report") continue;
            foreach (var file in EnumerateSourceFiles(directory)) yield return file;
        }
    }
}
