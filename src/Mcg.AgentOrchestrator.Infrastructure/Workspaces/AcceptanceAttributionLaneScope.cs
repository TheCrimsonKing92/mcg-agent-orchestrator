using System.Security.Cryptography;
using System.Text;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The failed checks whose reproduction can change a cohort attribution verdict.</summary>
public sealed class AcceptanceAttributionLaneScope
{
    private AcceptanceAttributionLaneScope(IEnumerable<string> names)
    {
        Names = Array.AsReadOnly(names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Names))));
    }

    public IReadOnlyList<string> Names { get; }
    public string Sha256 { get; }

    // The caller supplies the combined gate's non-advisory failed checks.
    public static AcceptanceAttributionLaneScope? FromCombinedFailedChecks(IReadOnlyList<string> failedChecks) =>
        failedChecks.Any(name => name.StartsWith("infrastructure tests: ", StringComparison.Ordinal))
            ? new(failedChecks.Where(name => name is not "infrastructure tests" && name != AcceptanceLaneEarlyStop.ReceiptName))
            : null;

    internal static (IReadOnlyList<AcceptanceManifestCheck> Checks, bool StructuralCoverageApplies) Apply(
        IReadOnlyList<AcceptanceManifestCheck> planChecks,
        IReadOnlyList<AcceptanceManifestCheck> candidateLaneChecks,
        bool structuralCoverageApplies,
        AcceptanceAttributionLaneScope? scope,
        TextWriter log)
    {
        if (scope is null) return (planChecks, structuralCoverageApplies);
        var available = planChecks.Concat(candidateLaneChecks).Select(check => check.Name).ToHashSet(StringComparer.Ordinal);
        var unresolved = scope.Names.FirstOrDefault(name => !available.Contains(name));
        if (unresolved is not null)
        {
            log.WriteLine($"ATTRIBUTION_LANE_SCOPE status=unresolved name={unresolved} fallback=full-plan");
            return (planChecks, structuralCoverageApplies);
        }

        var names = scope.Names.ToHashSet(StringComparer.Ordinal);
        var checks = planChecks.Where(check => names.Contains(check.Name)).ToList();
        var included = checks.Select(check => check.Name).ToHashSet(StringComparer.Ordinal);
        checks.AddRange(candidateLaneChecks.Where(check => names.Contains(check.Name) && included.Add(check.Name)));
        log.WriteLine($"ATTRIBUTION_LANE_SCOPE status=applied checks={checks.Count} names={string.Join(',', checks.Select(check => check.Name))}");
        return (checks, structuralCoverageApplies && names.Contains("structural test coverage"));
    }
}
