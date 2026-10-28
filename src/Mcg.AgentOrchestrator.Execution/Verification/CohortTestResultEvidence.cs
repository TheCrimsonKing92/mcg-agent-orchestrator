using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Shared by initial classification and persisted receipt readback. Never probes another format.
public static class CohortTestResultEvidence
{
    public static int CountExecutedTrx(IReadOnlyList<string> paths) => paths.Sum(path =>
        new TrxTestResultSource().Read(path).Records.Count(record => record.Status != TestResultStatus.Skipped));

    public sealed record Assessment(bool Coherent, bool Passed, int ExecutedTestCount);

    public static AcceptanceCohortEvidenceArtifact Capture(string path, string format)
    {
        using var stream = File.OpenRead(path);
        return new(format, Path.GetFullPath(path), Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }

    public static Assessment Assess(IReadOnlyList<AcceptanceCohortCheckEvidence> checks)
    {
        if (checks.Count == 0 || checks.Select(c => c.CheckName).Distinct(StringComparer.Ordinal).Count() != checks.Count)
            return new(false, false, 0);
        var total = 0;
        var passed = true;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var check in checks)
            {
                if (string.IsNullOrWhiteSpace(check.CheckName) || string.IsNullOrWhiteSpace(check.AttemptId) ||
                    check.RunOrdinal < 0 || check.ExitCode is null || check.Artifacts.Count == 0 ||
                    check.Format is not (AcceptanceCohortCheckEvidence.NormalizedFormat or AcceptanceCohortCheckEvidence.TrxFormat))
                    return new(false, false, total);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var executed = new HashSet<string>(StringComparer.Ordinal);
                var count = 0;
                foreach (var supplemental in check.SupplementalArtifacts)
                {
                    if (supplemental.Kind is not (AcceptanceCohortCheckEvidence.NormalizedFormat or AcceptanceCohortCheckEvidence.TrxFormat) ||
                        !paths.Add(supplemental.Path) || !IsOwnedPath(check.ArtifactRoot, supplemental.Path) ||
                        !AcceptanceCohortGateEvidence.IsContentBoundArtifact(supplemental)) return new(false, false, total);
                    if (supplemental.Kind == AcceptanceCohortCheckEvidence.TrxFormat &&
                        !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([supplemental.Path])) return new(false, false, total);
                    var read = supplemental.Kind == AcceptanceCohortCheckEvidence.NormalizedFormat
                        ? new NormalizedTestResultSource().Read(supplemental.Path) : new TrxTestResultSource().Read(supplemental.Path);
                    if (read.Status != TestResultReadStatus.Readable ||
                        read.Records.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != read.Records.Count ||
                        read.Records.Count(r => r.Status != TestResultStatus.Skipped) != check.ExecutedTestCount)
                        return new(false, false, total);
                    if (read.Records.Any(r => r.Status is TestResultStatus.Failed or TestResultStatus.Error)) passed = false;
                }
                foreach (var artifact in check.Artifacts)
                {
                    if (artifact.Kind != check.Format || !paths.Add(artifact.Path) ||
                        !IsOwnedPath(check.ArtifactRoot, artifact.Path) ||
                        !AcceptanceCohortGateEvidence.IsContentBoundArtifact(artifact)) return new(false, false, total);
                    var normalized = check.Format == AcceptanceCohortCheckEvidence.NormalizedFormat;
                    if (!normalized && !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([artifact.Path]))
                        return new(false, false, total);
                    var read = normalized ? new NormalizedTestResultSource().Read(artifact.Path) : new TrxTestResultSource().Read(artifact.Path);
                    if (read.Status != TestResultReadStatus.Readable) return new(false, false, total);
                    foreach (var record in read.Records)
                    {
                        if (string.IsNullOrWhiteSpace(record.Id) || !ids.Add(record.Id)) return new(false, false, total);
                        if (record.Status is TestResultStatus.Failed or TestResultStatus.Error) passed = false;
                        if (record.Status != TestResultStatus.Skipped) { count++; executed.Add(record.Id); }
                    }
                }
                if (count <= 0 || count != check.ExecutedTestCount ||
                    check.SelectedTestIds.Any(string.IsNullOrWhiteSpace) ||
                    check.SelectedTestIds.Distinct(StringComparer.Ordinal).Count() != check.SelectedTestIds.Count ||
                    check.SelectedTestIds.Any(id => !executed.Contains(id)) ||
                    (check.Format == AcceptanceCohortCheckEvidence.NormalizedFormat && check.SelectedTestIds.Count == 0))
                    return new(false, false, total);
                passed &= check.ExitCode == 0;
                total = checked(total + count);
            }
            return new(true, passed, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException) { return new(false, false, total); }
    }

    public static bool IsOwnedPath(string root, string path)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path)) return false;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        // Acceptance allocates these files. Refuse links in the entire chain before opening/copying.
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
