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
            .Where(correction => string.Equals(correction.Actor, "operator", StringComparison.OrdinalIgnoreCase) ||
                correction.Actor.StartsWith("operator:", StringComparison.OrdinalIgnoreCase))
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
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", snapshot))))
            .ToLowerInvariant();
    }
}
