using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public static class EffectiveAcceptanceCriteriaVersion
{
    public static IReadOnlyList<string> BuildSnapshot(
        RefinedSpec spec,
        IEnumerable<EffectiveAcceptanceCriteriaCorrection> corrections)
    {
        var latest = corrections
            .OrderBy(correction => correction.RecordedAt)
            .GroupBy(correction => correction.SupersededCriterion.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return spec.AcceptanceCriteria
            .Select(criterion => criterion.Trim())
            .Select(criterion => latest.TryGetValue(criterion, out var correction)
                ? correction.IsWaiver
                    ? $"[WAIVED] {criterion} — operator rationale: {correction.WaiverReason}"
                    : correction.Correction.Trim()
                : criterion)
            .ToArray();
    }

    public static string ComputeHash(
        RefinedSpec spec,
        IEnumerable<EffectiveAcceptanceCriteriaCorrection> corrections)
    {
        var snapshot = BuildSnapshot(spec, corrections);
        return ComputeSnapshotHash(snapshot);
    }

    public static bool IsCapturedHashCurrent(
        RefinedSpec spec,
        IEnumerable<EffectiveAcceptanceCriteriaCorrection> corrections,
        string? capturedHash)
    {
        if (string.IsNullOrWhiteSpace(capturedHash))
            return false;

        var materializedCorrections = corrections.ToArray();
        return string.Equals(capturedHash, ComputeHash(spec, materializedCorrections), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capturedHash, ComputeLegacyHash(spec, materializedCorrections), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeLegacyHash(
        RefinedSpec spec,
        IEnumerable<EffectiveAcceptanceCriteriaCorrection> corrections)
    {
        var waivedCriteria = corrections
            .Where(correction => correction.IsWaiver)
            .Select(correction => correction.SupersededCriterion.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = spec.AcceptanceCriteria
            .Select(criterion => criterion.Trim())
            .Select(criterion => waivedCriteria.Contains(criterion) ? $"[WAIVED] {criterion}" : criterion);
        return ComputeSnapshotHash(snapshot);
    }

    private static string ComputeSnapshotHash(IEnumerable<string> snapshot) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", snapshot))))
            .ToLowerInvariant();
}
