using System.Text.Json;
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
                        observation.FlakeConfirmed, failingClasses);
                }).ToArray();
            var record = new ShadowRecord(mainSha, candidateTreeSha, verifyingCommitSha, changedPaths?.Count,
                statusCause is null ? "resolved" : $"unavailable:{statusCause}", classification.IgnoredPaths,
                rows, new Summary(rows.Length, rows.Count(row => row.Decision == "would-reuse"), rows.Count(row => row.Decision == "must-run"),
                    rows.Count(row => row.Decision == "would-reuse" && row.Executed), rows.Count(row => row.ShadowMiss)),
                DateTimeOffset.UtcNow, mainTreeSha, unreadableReferenceRecords);
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
        bool? FlakeConfirmed, IReadOnlyList<string> FailingClasses);
    private sealed record Summary(int LaneCount, int WouldReuseCount, int MustRunCount,
        int WouldReuseExecutedCount, int ShadowMissCount);
    private sealed record ShadowRecord(string MainSha, string CandidateTreeSha, string VerifyingCommitSha,
        int? ChangedFileCount, string Status, IReadOnlyList<string> IgnoredPaths, IReadOnlyList<LaneRow> Lanes, Summary Summary,
        DateTimeOffset RecordedAt, string? MainTreeSha, int UnreadableReferenceRecords);
}
