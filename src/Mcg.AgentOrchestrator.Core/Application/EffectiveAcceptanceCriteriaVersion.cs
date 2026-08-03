using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public static class EffectiveAcceptanceCriteriaVersion
{
    public static IReadOnlyList<string> BuildSnapshot(
        RefinedSpec spec,
        IEnumerable<EffectiveAcceptanceCriteriaCorrection> corrections)
    {
        var waivedCriteria = corrections
            .Where(correction => correction.IsWaiver)
            .Select(correction => correction.SupersededCriterion.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return spec.AcceptanceCriteria
            .Select(criterion => criterion.Trim())
            .Select(criterion => waivedCriteria.Contains(criterion) ? $"[WAIVED] {criterion}" : criterion)
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
