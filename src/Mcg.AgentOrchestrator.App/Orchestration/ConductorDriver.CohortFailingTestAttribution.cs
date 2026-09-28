using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static IReadOnlyList<string> CohortFailingTestIdentities(AcceptanceVerificationResult result) =>
        result.Checks?
            .Where(check => !check.Passed && !check.Advisory)
            .SelectMany(check => check.FailingTestIdentities ?? [])
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

    internal IReadOnlySet<string> ReadCohortAttributedMemberKeys() =>
        _cohortAcceptanceStore?.ReadAttributedMemberKeys() ?? new HashSet<string>(StringComparer.Ordinal);
}
