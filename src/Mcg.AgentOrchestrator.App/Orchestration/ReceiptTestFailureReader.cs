using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ReceiptTestFailureReader
{
    internal static IReadOnlyList<AcceptanceTrxFailure> ReadFromTrx(AcceptanceCheckResult check)
    {
        if (check.TestResultPaths is not { Count: > 0 } paths)
        {
            return [];
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path => AcceptanceTrxFailureReader.Read(path).Failures)
            .ToArray();
    }

    internal static bool TryReadNormalized(
        AcceptanceCheckResult check, out IReadOnlyList<AcceptanceTrxFailure> failures)
    {
        failures = [];
        try
        {
            var trxPaths = check.TestResultPaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            var normalizedPaths = check.NormalizedTestResultPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            if (trxPaths is not { Length: 1 } || normalizedPaths is not { Length: 1 })
            {
                return false;
            }

            if (!string.Equals(Path.GetFullPath(normalizedPaths[0]),
                Path.GetFullPath(NormalizedTestResultWriter.PathFor(trxPaths[0])),
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var result = new NormalizedTestResultSource().Read(normalizedPaths[0]);
            if (result.Status != TestResultReadStatus.Readable ||
                result.Records.Any(record => record.Status is not (TestResultStatus.Passed or TestResultStatus.Failed)))
            {
                return false;
            }

            failures = result.Records
                .Where(record => record.Status == TestResultStatus.Failed)
                .Select(record => new AcceptanceTrxFailure(record.Id, "Failed", record.Message, record.Trace))
                .ToArray();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<AcceptanceTrxFailure> Read(AcceptanceCheckResult check) =>
        TryReadNormalized(check, out var failures) ? failures : ReadFromTrx(check);
}
