using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceFailingTestIndexKinds
{
    internal const string GateFailure = "gate-failure";
    internal const string ApparatusRegate = "apparatus-regate";
}

/// <summary>
/// One line of the cross-goal failing-test census. A single record shape with a kind discriminator
/// keeps both record families cheap to read in one pass, which is what lets the per-goal re-gate
/// bound be counted at the same moment the classifier already reads the store.
/// </summary>
internal sealed record AcceptanceFailingTestIndexRecord(
    int ContractVersion,
    string Kind,
    string GoalId,
    DateTimeOffset RecordedAt,
    string? CandidateSha = null,
    string? CheckName = null,
    string? TestIdentity = null,
    string? ExceptionSignature = null,
    string? ResolvedSourcePath = null,
    bool InsideChangedPaths = false,
    string? EvidenceKind = null,
    string? MessageFingerprint = null);

internal sealed record AcceptanceFailingTestCensusRow(
    string TestIdentity,
    int DistinctGoals,
    int OutsideChangedPathsFailures,
    int TotalFailures,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// The durable failing-test census. It lives at the root of the acceptance-gate-attempts family,
/// beside the per-goal attempt directories rather than inside one: both retention walkers enumerate
/// directories, so a root-level file survives per-goal cleanup, which is exactly what cross-goal
/// flake detection requires.
///
/// Every operation is best effort. An index failure must never fail or alter a gate outcome.
/// </summary>
internal sealed class AcceptanceFailingTestIndex
{
    internal const int ContractVersion = 1;
    internal const string FileName = "failing-test-index.v1.jsonl";

    // Bounds pathological growth. Census records are evicted oldest-first; re-gate records are never
    // evicted by age, because pruning them would silently reset the per-goal bound.
    internal const int MaximumRecords = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex AbsolutePath = new(@"(?<![\w.])(?:[A-Za-z]:[\\/]|\\\\|//|/)[^\s<>""',;:?!]+", RegexOptions.Compiled);
    private static readonly Regex Id = new(@"\b(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{40}|[0-9a-fA-F]{32})\b", RegexOptions.Compiled);
    private static readonly Regex Timestamp = new(@"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?\b", RegexOptions.Compiled);
    private static readonly Regex Duration = new(@"(?<![\w.])\d+(?:\.\d+)?(?:milliseconds|minutes|seconds|msec|ms|sec|min|s)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly string _path;
    private readonly TimeSpan _censusRetention;
    private readonly object _pruneGate = new();

    internal AcceptanceFailingTestIndex(string path, TimeSpan? censusRetention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _censusRetention = censusRetention ?? TimeSpan.FromDays(14);
    }

    internal string Path => _path;

    internal static string NormalizeFailureMessage(string message)
    {
        var normalized = AbsolutePath.Replace(message, "<path>");
        normalized = Id.Replace(normalized, "<id>");
        normalized = Timestamp.Replace(normalized, "<time>");
        normalized = Duration.Replace(normalized, "<dur>");
        return Whitespace.Replace(normalized, " ").Trim();
    }

    internal static string? ComputeMessageFingerprint(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = NormalizeFailureMessage(message);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized[..Math.Min(2000, normalized.Length)])));
    }

    internal void Append(IReadOnlyList<AcceptanceFailingTestIndexRecord> records, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            SharedJsonlFile.AppendLines(
                _path,
                records.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
            Prune(now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The census is an accelerator, never a gate input of record.
        }
    }

    internal IReadOnlyList<AcceptanceFailingTestIndexRecord> Read()
    {
        var records = new List<AcceptanceFailingTestIndexRecord>();
        try
        {
            foreach (var line in SharedJsonlFile.ReadAllLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<AcceptanceFailingTestIndexRecord>(line, JsonOptions) is
                        { ContractVersion: ContractVersion } record)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // A partially written or corrupt line must not hide the rest of the census.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return records;
    }

    /// <summary>
    /// True when the same test identity already failed a gate under a <em>different</em> goal inside
    /// the window, on an attempt whose own candidate did not touch that test's source.
    /// </summary>
    internal static bool HasCrossGoalOccurrence(
        IReadOnlyList<AcceptanceFailingTestIndexRecord> records,
        string goalId,
        string testIdentity,
        string? messageFingerprint,
        DateTimeOffset now,
        TimeSpan window) =>
        !string.IsNullOrWhiteSpace(messageFingerprint) && records.Any(record =>
            record.Kind.Equals(AcceptanceFailingTestIndexKinds.GateFailure, StringComparison.Ordinal) &&
            !record.InsideChangedPaths &&
            !record.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.TestIdentity, testIdentity, StringComparison.Ordinal) &&
            string.Equals(record.MessageFingerprint, messageFingerprint, StringComparison.Ordinal) &&
            now - record.RecordedAt <= window &&
            record.RecordedAt <= now);

    internal static int CountRegates(
        IReadOnlyList<AcceptanceFailingTestIndexRecord> records,
        string goalId) =>
        records.Count(record =>
            record.Kind.Equals(AcceptanceFailingTestIndexKinds.ApparatusRegate, StringComparison.Ordinal) &&
            record.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<AcceptanceFailingTestCensusRow> BuildCensus(
        IReadOnlyList<AcceptanceFailingTestIndexRecord> records,
        int minimumGoals = 2,
        DateTimeOffset? since = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (minimumGoals < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumGoals));
        }

        return records
            .Where(record =>
                record.Kind.Equals(AcceptanceFailingTestIndexKinds.GateFailure, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(record.TestIdentity) &&
                (since is null || record.RecordedAt >= since.Value))
            .GroupBy(record => record.TestIdentity!, StringComparer.Ordinal)
            .Select(group => new AcceptanceFailingTestCensusRow(
                group.Key,
                group.Select(record => record.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                group.Count(record => !record.InsideChangedPaths),
                group.Count(),
                group.Min(record => record.RecordedAt),
                group.Max(record => record.RecordedAt)))
            .Where(row => row.DistinctGoals >= minimumGoals)
            .OrderByDescending(row => row.DistinctGoals)
            .ThenByDescending(row => row.TotalFailures)
            .ThenBy(row => row.TestIdentity, StringComparer.Ordinal)
            .ToArray();
    }

    private void Prune(DateTimeOffset now)
    {
        if (!Monitor.TryEnter(_pruneGate))
        {
            // Another append is already rewriting the file; skipping keeps the concurrent-gate path
            // append-only, which is the safe direction.
            return;
        }

        try
        {
            var records = Read();
            var retained = records
                .Where(record =>
                    !record.Kind.Equals(AcceptanceFailingTestIndexKinds.GateFailure, StringComparison.Ordinal) ||
                    now - record.RecordedAt <= _censusRetention)
                .ToList();
            if (retained.Count > MaximumRecords)
            {
                var censusOverflow = retained.Count - MaximumRecords;
                var evicted = retained
                    .Where(record => record.Kind.Equals(
                        AcceptanceFailingTestIndexKinds.GateFailure,
                        StringComparison.Ordinal))
                    .OrderBy(record => record.RecordedAt)
                    .Take(censusOverflow)
                    .ToHashSet();
                retained = retained.Where(record => !evicted.Contains(record)).ToList();
            }

            if (retained.Count == records.Count)
            {
                return;
            }

            var temporaryPath = _path + ".prune-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllLines(
                temporaryPath,
                retained.Select(record => JsonSerializer.Serialize(record, JsonOptions)));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Pruning is maintenance; a busy file simply keeps the existing census.
        }
        finally
        {
            Monitor.Exit(_pruneGate);
        }
    }
}
