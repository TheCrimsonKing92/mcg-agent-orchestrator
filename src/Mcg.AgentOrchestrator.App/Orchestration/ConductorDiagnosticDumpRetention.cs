using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorDiagnosticDumpRetention
{
    internal const int KeepNewest = 2;
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromDays(3);

    private static readonly Regex DumpName = new(
        @"^conductor-tick-stall-[0-9]+-(?<stamp>[0-9]{17})\.dmp$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static void Sweep(string logDirectory, DateTimeOffset now, List<EvidenceRetentionDecision> decisions)
    {
        if (!Directory.Exists(logDirectory)) return;

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => DumpName.IsMatch(Path.GetFileName(path)))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DiagnosticDumps,
                EvidenceRetentionAction.DeferredLocked, logDirectory, null, EvidenceOwnerResolution.Unrecorded,
                "diagnostic-dump-enumeration-failed", FailureExceptionType: ex.GetType().Name));
            return;
        }

        var parsed = new List<(string Path, string Name, DateTimeOffset Stamp)>();
        var unreadable = new List<string>();
        foreach (var path in candidates)
        {
            var name = Path.GetFileName(path);
            if (DateTimeOffset.TryParseExact(DumpName.Match(name).Groups["stamp"].Value,
                "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp))
            {
                parsed.Add((path, name, stamp));
            }
            else
            {
                unreadable.Add(path);
            }
        }

        var ranked = parsed.OrderByDescending(candidate => candidate.Stamp)
            .ThenByDescending(candidate => candidate.Name, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ranked.Length; index++)
        {
            var candidate = ranked[index];
            if (index < KeepNewest || now - candidate.Stamp <= MinimumAge)
            {
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DiagnosticDumps,
                    EvidenceRetentionAction.Preserved, candidate.Path, null, EvidenceOwnerResolution.Unrecorded,
                    index < KeepNewest ? "diagnostic-dump-newest" : "diagnostic-dump-within-age"));
                continue;
            }

            var length = StorageRetentionMaintenance.SafeLength(candidate.Path);
            var deletion = StorageRetentionMaintenance.TryDeleteExclusive(candidate.Path);
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DiagnosticDumps,
                deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                candidate.Path, null, EvidenceOwnerResolution.Unrecorded,
                deletion.Success ? "diagnostic-dump-past-age" : "diagnostic-dump-locked",
                BytesAttempted: length, BytesReclaimed: deletion.Success ? length : 0,
                FailureExceptionType: deletion.ExceptionType));
        }

        foreach (var path in unreadable.OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.DiagnosticDumps,
                EvidenceRetentionAction.Preserved, path, null, EvidenceOwnerResolution.Unrecorded,
                "diagnostic-dump-stamp-unreadable"));
        }
    }
}
