using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static IReadOnlyList<string> CohortFailingTestIdentities(AcceptanceVerificationResult result)
    {
        var fromChecks = result.Checks?
            .Where(check => !check.Passed && !check.Advisory)
            .SelectMany(check => check.FailingTestIdentities ?? [])
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

        if (fromChecks.Length > 0 || result.Passed)
        {
            return fromChecks;
        }

        var paths = result.TestResultPaths ?? result.Checks?
            .Where(check => !check.Passed && !check.Advisory)
            .SelectMany(check => check.TestResultPaths ?? [])
            .ToArray() ?? [];
        return paths
            .Select(AcceptanceTrxFailureReader.Read)
            .Where(read => read.Status == AcceptanceTrxReadStatus.Readable)
            .SelectMany(read => read.Failures)
            .Select(failure => failure.TestName)
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Select(identity => identity!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal IReadOnlySet<string> ReadCohortAttributedMemberKeys() =>
        _cohortAcceptanceStore?.ReadAttributedMemberKeys() ?? new HashSet<string>(StringComparer.Ordinal);
}
