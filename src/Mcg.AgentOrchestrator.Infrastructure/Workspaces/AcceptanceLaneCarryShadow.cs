using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Attempt-scoped measurement only; neither the cache nor the verifier consumes these records.
internal sealed class AcceptanceLaneCarryShadow
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] TierNames = ["optimistic", "mid", "conservative"];
    private const string BaselinesPhase = "baselines", ScanPhase = "scan", DiffPhase = "diff",
        IndexPhase = "index", LanesPhase = "lanes", WritePhase = "write";
    private readonly object _gate = new();
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.Ordinal);
    private readonly string _worktreePath, _goalId, _attemptId, _candidateTree;
    private readonly Func<string, string, string, IReadOnlyList<string>> _resolveChangedFiles;
    private readonly Func<long> _budgetTimestamp;
    private bool _completed;

    internal AcceptanceLaneCarryShadow(string worktreePath, string goalId, string attemptId, string candidateTree,
        Func<string, string, string, IReadOnlyList<string>>? resolveChangedFiles = null,
        Func<long>? budgetTimestamp = null)
    {
        _worktreePath = worktreePath;
        _goalId = goalId;
        _attemptId = attemptId;
        _candidateTree = candidateTree;
        _resolveChangedFiles = resolveChangedFiles ?? ResolveChangedFilesFromGit;
        _budgetTimestamp = budgetTimestamp ?? Stopwatch.GetTimestamp;
    }

    internal static IReadOnlyList<string> ResolveChangedFilesFromGit(string root, string baseline, string candidate)
    {
        var result = GitCli.Run(root, 5000, "diff", "--name-only", "--no-renames", "-z", baseline, candidate, "--");
        if (!result.Succeeded || result.DrainTimedOut)
            throw new IOException($"git diff failed: exit={result.ExitCode}, drain_timeout={result.DrainTimedOut}");
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal void Observe(AcceptanceManifestCheck check, string filterHash, AcceptanceCheckResult result)
    {
        try
        {
            var classes = new SortedSet<string>(StringComparer.Ordinal);
            var unresolved = new SortedSet<string>(StringComparer.Ordinal);
            var passed = result.Passed;
            foreach (var path in result.TestResultPaths ?? [])
            {
                try
                {
                    var document = XDocument.Load(path);
                    var definitions = document.Descendants().Where(node => node.Name.LocalName == "UnitTest")
                        .Where(node => node.Attribute("id") is not null)
                        .GroupBy(node => node.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (var test in document.Descendants().Where(node => node.Name.LocalName == "UnitTestResult"))
                    {
                        if (AcceptanceTrxOutcomeTaxonomy.IsFatal(test.Attribute("outcome")?.Value)) passed = false;
                        definitions.TryGetValue(test.Attribute("testId")?.Value ?? "", out var definition);
                        var name = definition?.Descendants().FirstOrDefault(node => node.Name.LocalName == "TestMethod")
                            ?.Attribute("className")?.Value;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            unresolved.Add("<missing-trx-class>");
                            Log("missing-trx-class");
                        }
                        else classes.Add(name);
                    }
                }
                catch (Exception ex)
                {
                    passed = false;
                    unresolved.Add("<unreadable-trx>");
                    Log($"trx:{ex.GetType().Name}");
                }
            }
            lock (_gate)
                _observations[filterHash] = new(filterHash, passed && classes.Count > 0 ? "passed" : "failed",
                    classes.ToArray(), unresolved.ToArray());
        }
        catch (Exception ex) { Log($"observation:{ex.GetType().Name}"); }
    }

    internal void Complete()
    {
        long start = 0;
        string? path = null;
        string? temporaryPath = null;
        try
        {
            start = _budgetTimestamp();
            Observation[] observations;
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                observations = _observations.Values.OrderBy(row => row.FilterHash, StringComparer.Ordinal).ToArray();
            }
            var startedAt = DateTimeOffset.UtcNow;
            var root = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(_worktreePath),
                ".orchestrator", "lane-carry-shadow");
            path = Path.Combine(root, SafeName(_goalId), $"{SafeName(_attemptId)}.json");
            var (baselines, skipped) = ReadBaselines(root, path, startedAt, start);
            CheckBudget(BaselinesPhase, start);
            var sources = ScanSources(observations.Length > 0);
            CheckBudget(ScanPhase, start);
            var diffs = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
            foreach (var tree in observations.Select(row => baselines.GetValueOrDefault(row.FilterHash)?.Tree)
                         .OfType<string>().Distinct(StringComparer.Ordinal))
            {
                CheckBudget(DiffPhase, start);
                try { diffs[tree] = _resolveChangedFiles(_worktreePath, tree, _candidateTree); }
                catch (Exception ex)
                {
                    diffs[tree] = null;
                    Log($"baseline-tree-missing:{ex.GetType().Name}");
                }
                CheckBudget(DiffPhase, start);
            }
            var sourceIndex = BuildSourceIndex(diffs.Values.OfType<IReadOnlyList<string>>()
                .SelectMany(files => files).Any(AcceptanceLaneCarryShadowReach.IsSource), start);
            var changes = new Dictionary<string, AcceptanceLaneCarryShadowReach.ChangeSet>(StringComparer.Ordinal);
            foreach (var (tree, files) in diffs.Where(pair => pair.Value is not null))
            {
                changes[tree] = AcceptanceLaneCarryShadowReach.Prepare(files!, sourceIndex);
                CheckBudget(IndexPhase, start);
            }
            var lanes = new List<Lane>();
            foreach (var observation in observations)
            {
                var unresolved = new SortedSet<string>(observation.Unresolved, StringComparer.Ordinal);
                var laneSources = new List<AcceptanceTestClassSource>();
                foreach (var name in observation.Classes)
                {
                    var normalized = name.Split('+')[0].Split('`')[0];
                    if (sources.TryGetValue(normalized, out var source)) laneSources.Add(source);
                    else
                    {
                        unresolved.Add(name);
                        Log($"unresolved-class:{name}");
                    }
                }
                var baseline = baselines.GetValueOrDefault(observation.FilterHash);
                var status = baseline is null ? "no-baseline" :
                    diffs[baseline.Tree] is null ? "baseline-tree-missing" : "found";
                var reach = status == "found" ? AcceptanceLaneCarryShadowReach.Evaluate(changes[baseline!.Tree],
                    laneSources.SelectMany(source => source.SourcePaths).Distinct(StringComparer.Ordinal).ToArray(),
                    string.Join("\n", laneSources.Select(source => source.SourceText)),
                    unresolved.Count > 0 || observation.Classes.Length == 0) : new(true, true, true);
                var tiers = new Dictionary<string, Tier>(StringComparer.Ordinal)
                {
                    ["optimistic"] = MakeTier(reach.Optimistic, status, observation.Outcome),
                    ["mid"] = MakeTier(reach.Mid, status, observation.Outcome),
                    ["conservative"] = MakeTier(reach.Conservative, status, observation.Outcome)
                };
                lanes.Add(new(observation.FilterHash, observation.Outcome, observation.Classes, unresolved.ToArray(),
                    status, baseline?.Attempt, baseline?.Tree, tiers));
                CheckBudget(LanesPhase, start);
            }
            var summary = TierNames.ToDictionary(tier => tier,
                tier => new Summary(lanes.Count, lanes.Count(lane => lane.Tiers[tier].WouldCarry),
                    lanes.Count(lane => lane.Tiers[tier].FalseCarry)), StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            CheckBudget(WritePhase, start);
            var record = new ShadowRecord(_goalId, _attemptId, _candidateTree, DateTimeOffset.UtcNow,
                ElapsedMs(start), skipped, lanes, summary);
            temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, JsonOptions));
            CheckBudget(WritePhase, start);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (BudgetExceededException ex)
        {
            Log($"budget-exceeded:{ex.Phase}");
            try
            {
                var record = new BudgetExceededRecord(_goalId, _attemptId, _candidateTree,
                    DateTimeOffset.UtcNow, ElapsedMs(start), ex.Phase, []);
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                temporaryPath ??= $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, JsonOptions));
                File.Move(temporaryPath, path!, overwrite: true);
            }
            catch (Exception failure) { Log($"completion:{failure.GetType().Name}"); }
        }
        catch (Exception ex) { Log($"completion:{ex.GetType().Name}"); }
        finally
        {
            if (temporaryPath is not null)
                try { File.Delete(temporaryPath); }
                catch (Exception ex) { Log($"cleanup:{ex.GetType().Name}"); }
        }
    }

    private Dictionary<string, AcceptanceTestClassSource> ScanSources(bool needed)
    {
        try
        {
            return needed ? AcceptanceTestClassSourceScanner.ScanSources(_worktreePath)
                .ToDictionary(source => source.FullName, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log($"scan:{ex.GetType().Name}");
            return new(StringComparer.Ordinal);
        }
    }

    private Dictionary<string, AcceptanceLaneCarryShadowReach.Source> BuildSourceIndex(bool needed, long start)
    {
        var sources = new Dictionary<string, AcceptanceLaneCarryShadowReach.Source>(StringComparer.Ordinal);
        var root = Path.Combine(_worktreePath, "src");
        if (!needed || !Directory.Exists(root)) return sources;
        foreach (var path in EnumerateSourceFiles(root))
        {
            CheckBudget(IndexPhase, start);
            sources[Path.GetRelativePath(_worktreePath, path).Replace('\\', '/')] =
                AcceptanceLaneCarryShadowReach.IndexSource(File.ReadAllText(path));
        }
        CheckBudget(IndexPhase, start);
        return sources;
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

    private (Dictionary<string, Baseline> Baselines, int Skipped) ReadBaselines(
        string root, string currentPath, DateTimeOffset before, long start)
    {
        var baselines = new Dictionary<string, Baseline>(StringComparer.Ordinal);
        var skipped = 0;
        if (!Directory.Exists(root)) return (baselines, skipped);
        var newest = new PriorityQueue<string, (long Time, string Path)>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            CheckBudget(BaselinesPhase, start);
            if (path.Equals(currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                newest.Enqueue(path, (File.GetLastWriteTimeUtc(path).Ticks, path));
                if (newest.Count > 200) newest.Dequeue();
            }
            catch (Exception) { skipped++; }
        }
        foreach (var file in newest.UnorderedItems)
        {
            CheckBudget(BaselinesPhase, start);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file.Element));
                var record = document.RootElement;
                var completed = record.GetProperty("completed_at").GetDateTimeOffset();
                var tree = record.GetProperty("candidate_tree").GetString();
                var attempt = record.GetProperty("attempt_id").GetString();
                if (string.IsNullOrWhiteSpace(tree) || string.IsNullOrWhiteSpace(attempt)) throw new InvalidDataException();
                var rows = record.GetProperty("lanes").EnumerateArray().Select(lane => (
                    Filter: lane.GetProperty("filter_hash").GetString(), Outcome: lane.GetProperty("outcome").GetString())).ToArray();
                if (rows.Any(row => string.IsNullOrWhiteSpace(row.Filter) || row.Outcome is not ("passed" or "failed")))
                    throw new InvalidDataException();
                if (completed >= before) continue;
                foreach (var row in rows.Where(row => row.Outcome == "passed"))
                    if (!baselines.TryGetValue(row.Filter!, out var prior) || prior.Completed < completed)
                        baselines[row.Filter!] = new(tree, attempt, completed);
            }
            catch (Exception) { skipped++; }
        }
        return (baselines, skipped);
    }

    private static Tier MakeTier(bool reached, string status, string outcome)
    {
        var carry = status == "found" && !reached;
        return new(reached, carry, carry && outcome == "failed");
    }

    private long ElapsedMs(long start) => (long)Stopwatch.GetElapsedTime(start, _budgetTimestamp()).TotalMilliseconds;

    private void CheckBudget(string phase, long start)
    {
        if (Stopwatch.GetElapsedTime(start, _budgetTimestamp()) > TimeSpan.FromSeconds(10))
            throw new BudgetExceededException(phase);
    }

    private static string SafeName(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static void Log(string cause)
    {
        try { Console.WriteLine($"LANE_CARRY_SHADOW_UNAVAILABLE {cause}"); }
        catch (Exception) { }
    }

    private sealed class BudgetExceededException(string phase) : Exception
    {
        internal string Phase => phase;
    }
    private sealed record Observation(string FilterHash, string Outcome, string[] Classes, string[] Unresolved);
    private sealed record Baseline(string Tree, string Attempt, DateTimeOffset Completed);
    private sealed record Tier(bool Reached, bool WouldCarry, bool FalseCarry);
    private sealed record Lane(string FilterHash, string Outcome, string[] TestClasses, string[] UnresolvedClasses,
        string BaselineStatus, string? BaselineAttempt, string? BaselineTree, Dictionary<string, Tier> Tiers);
    private sealed record Summary(int LaneCount, int WouldCarryCount, int FalseCarryCount);
    private sealed record ShadowRecord(string GoalId, string AttemptId, string CandidateTree, DateTimeOffset CompletedAt,
        long ElapsedMs, int SkippedBaselineFiles, IReadOnlyList<Lane> Lanes, Dictionary<string, Summary> Summary);
    private sealed record BudgetExceededRecord(string GoalId, string AttemptId, string CandidateTree,
        DateTimeOffset CompletedAt, long ElapsedMs, string BudgetExceededPhase, IReadOnlyList<Lane> Lanes);
}
