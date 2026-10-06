using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Attempt-scoped observation only. Nothing in cache reuse, scheduling or verdicts reads this state.
internal sealed class AcceptanceLaneReuseShadow(
    string worktreePath, string goalId, string attemptId, string mainSha,
    string candidateTreeSha, string verifyingCommitSha, IReadOnlyList<AcceptanceManifestCheck> checks)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly object _gate = new();
    private readonly Dictionary<string, (AcceptanceCheckResult Result, bool? FlakeConfirmed)> _observed = new(StringComparer.Ordinal);

    internal void Observe(AcceptanceManifestCheck check, AcceptanceCheckResult result, bool? flakeConfirmed = null)
    {
        try
        {
            if (!GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _)) return;
            lock (_gate) _observed[check.Name] = (result, flakeConfirmed);
        }
        catch (Exception ex) { Log($"LANE_REUSE_SHADOW_UNAVAILABLE observation:{ex.GetType().Name}"); }
    }

    internal void Complete()
    {
        string? temporaryPath = null;
        try
        {
            IReadOnlyList<string>? changedPaths = null;
            string? unavailableCause = null;
            try { changedPaths = AcceptanceTestReuseShadow.ResolveChangedFilesFromGit(worktreePath, mainSha, verifyingCommitSha); }
            catch (Exception ex) { unavailableCause = $"changed-files:{ex.GetType().Name}"; }
            if (changedPaths is { Count: 0 }) unavailableCause = "no-changed-files";
            var statusCause = unavailableCause;

            var lookup = new ReverseDependencyTestImpactLookupResult(true, [], null, null);
            IReadOnlyList<AcceptanceTestClassSource> inventory = [];
            string? inventoryFailure = null;
            if (AcceptanceLaneReuseShadowClassifier.PathReason(changedPaths, unavailableCause) is null)
            {
                var paths = AcceptanceLaneReuseShadowClassifier.SelectLookupPaths(changedPaths!).LookupPaths;
                try { lookup = ReverseDependencyTestImpactReaderLookup.Find(worktreePath, paths); }
                catch (Exception ex) { lookup = new(false, [], ex.GetType().Name, ex.Message); }
                if (!lookup.Resolved) statusCause = $"dependency-index-degraded:{lookup.DegradationKind}";
                else
                {
                    try { inventory = AcceptanceTestClassSourceScanner.ScanSources(worktreePath); }
                    catch (Exception ex) { inventoryFailure = ex.GetType().Name; }
                }
            }
            var classification = AcceptanceLaneReuseShadowClassifier.Classify(changedPaths, checks, inventory, lookup,
                unavailableCause, inventoryFailure);
            Dictionary<string, RuleV2> ruleV2;
            Dictionary<string, RuleV2> UnavailableV2(Exception ex)
            {
                Log($"LANE_REUSE_SHADOW_UNAVAILABLE rule-v2:{ex.GetType().Name}");
                return classification.Lanes.ToDictionary(lane => lane.Lane, lane =>
                    new RuleV2("must-run", $"rule-v2-unavailable:{ex.GetType().Name}", new([], "unavailable", [], null)), StringComparer.Ordinal);
            }
            try
            {
                var contracts = inventory.Count == 0 ? [] : AcceptanceLaneReuseShadowLaunchContracts.Build(
                    AcceptanceLaneReuseShadowLaunchContracts.ReadSources(worktreePath), inventory);
                ruleV2 = AcceptanceLaneReuseShadowLaunchRule.Classify(changedPaths, checks, inventory, lookup, contracts,
                        unavailableCause, inventoryFailure)
                    .ToDictionary(lane => lane.Lane, lane => new RuleV2(lane.Decision, lane.Reason, lane.Provenance), StringComparer.Ordinal);
            }
            catch (Exception ex) { ruleV2 = UnavailableV2(ex); }
            if (inventoryFailure is not null) statusCause = $"class-inventory:{inventoryFailure}";
            if (statusCause is not null) Log($"LANE_REUSE_SHADOW_UNAVAILABLE {statusCause}");
            var recordRoot = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath),
                ".orchestrator", "lane-reuse-shadow");
            string? mainTreeSha = null;
            IReadOnlyList<AcceptanceLaneReuseShadowMiss.ReferenceRow> references = [];
            var unreadableReferenceRecords = 0;
            try
            {
                mainTreeSha = AcceptanceLaneReuseShadowReferenceReader.ResolveMainTreeSha(worktreePath, mainSha);
                (references, unreadableReferenceRecords) = AcceptanceLaneReuseShadowReferenceReader.Read(
                    recordRoot, SafeFileName(goalId), SafeFileName(attemptId), mainTreeSha);
            }
            catch (Exception ex) { Log($"LANE_REUSE_SHADOW_UNAVAILABLE reference:{ex.GetType().Name}"); }
            LaneRow[] rows;
            lock (_gate)
                rows = classification.Lanes.Select(lane =>
                {
                    _observed.TryGetValue(lane.Lane, out var observation);
                    var result = observation.Result;
                    var verdict = result is null ? null : result.Passed ? "GREEN" : "RED";
                    var miss = new AcceptanceLaneReuseShadowMiss.Result(false, null, null, false, null);
                    string[] failingClasses = [];
                    try
                    {
                        miss = AcceptanceLaneReuseShadowMiss.Evaluate(lane.Decision, result is not null, verdict,
                            AcceptanceLaneReuseShadowMiss.ResolveReference(references, lane.Lane, lane.PartitionId));
                        failingClasses = AcceptanceLaneReuseShadowMiss.ReduceFailingClasses(result?.FailingTestIdentities);
                    }
                    catch (Exception ex) { Log($"LANE_REUSE_SHADOW_UNAVAILABLE miss:{ex.GetType().Name}"); }
                    return new LaneRow(lane.Lane, lane.PartitionId, lane.Decision, lane.Reason, result is not null,
                        verdict, result?.DurationMilliseconds, miss.MissEvaluated, miss.ReferenceVerdict,
                        miss.ReferenceSource, miss.ShadowMiss, miss.MissReason, result?.CompletionDecision?.FailedPredicate,
                        observation.FlakeConfirmed, failingClasses, ruleV2[lane.Lane]);
                }).ToArray();
            Summary summaryV2;
            try
            {
                summaryV2 = new(rows.Length, rows.Count(row => row.RuleV2.Decision == "would-reuse"),
                    rows.Count(row => row.RuleV2.Decision == "must-run"),
                    rows.Count(row => row.RuleV2.Decision == "would-reuse" && row.Executed),
                    rows.Count(row => AcceptanceLaneReuseShadowMiss.Evaluate(row.RuleV2.Decision, row.Executed, row.Verdict,
                        AcceptanceLaneReuseShadowMiss.ResolveReference(references, row.Lane, row.PartitionId)).ShadowMiss));
            }
            catch (Exception ex)
            {
                ruleV2 = UnavailableV2(ex);
                rows = rows.Select(row => row with { RuleV2 = ruleV2[row.Lane] }).ToArray();
                summaryV2 = new(rows.Length, 0, rows.Length, 0, 0);
            }
            var record = new ShadowRecord(mainSha, candidateTreeSha, verifyingCommitSha, changedPaths?.Count,
                statusCause is null ? "resolved" : $"unavailable:{statusCause}", classification.IgnoredPaths,
                rows, new Summary(rows.Length, rows.Count(row => row.Decision == "would-reuse"), rows.Count(row => row.Decision == "must-run"),
                    rows.Count(row => row.Decision == "would-reuse" && row.Executed), rows.Count(row => row.ShadowMiss)),
                DateTimeOffset.UtcNow, mainTreeSha, unreadableReferenceRecords, ["marker-v1", "launch-contract-v2"], summaryV2);
            var directory = Path.Combine(recordRoot, SafeFileName(goalId));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{SafeFileName(attemptId)}.json");
            temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) { Log($"LANE_REUSE_SHADOW_WRITE_FAILED {ex.GetType().Name}"); }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) { Log($"LANE_REUSE_SHADOW_WRITE_FAILED cleanup:{ex.GetType().Name}"); }
            }
        }
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static void Log(string message)
    {
        try { Console.WriteLine(message); }
        catch (Exception) { }
    }

    private sealed record LaneRow(string Lane, string PartitionId, string Decision, string Reason,
        bool Executed, string? Verdict, long? DurationMs, bool MissEvaluated, string? ReferenceVerdict,
        string? ReferenceSource, bool ShadowMiss, string? MissReason, string? FailedPredicate,
        bool? FlakeConfirmed, IReadOnlyList<string> FailingClasses,
        [property: JsonPropertyName("rule_v2")] RuleV2 RuleV2);
    private sealed record RuleV2(string Decision, string Reason, AcceptanceLaneReuseShadowLaunchProvenance Provenance);
    private sealed record Summary(int LaneCount, int WouldReuseCount, int MustRunCount,
        int WouldReuseExecutedCount, int ShadowMissCount);
    private sealed record ShadowRecord(string MainSha, string CandidateTreeSha, string VerifyingCommitSha,
        int? ChangedFileCount, string Status, IReadOnlyList<string> IgnoredPaths, IReadOnlyList<LaneRow> Lanes, Summary Summary,
        DateTimeOffset RecordedAt, string? MainTreeSha, int UnreadableReferenceRecords,
        [property: JsonPropertyName("rule_versions")] IReadOnlyList<string> RuleVersions,
        [property: JsonPropertyName("summary_v2")] Summary SummaryV2);
}
