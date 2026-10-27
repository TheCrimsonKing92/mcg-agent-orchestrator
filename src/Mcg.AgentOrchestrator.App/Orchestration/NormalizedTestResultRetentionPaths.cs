using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class NormalizedTestResultRetentionPaths
{
    internal static IReadOnlyList<string?> WithNormalizedPeer(string? testResultPath)
    {
        if (!string.IsNullOrWhiteSpace(testResultPath) &&
            testResultPath.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return [testResultPath, NormalizedTestResultWriter.PathFor(testResultPath)];
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return [testResultPath];
            }
        }

        return [testResultPath];
    }
}
