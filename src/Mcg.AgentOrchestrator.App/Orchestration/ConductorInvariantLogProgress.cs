using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorInvariantLogProgress
{
    internal static bool TryGetProgress(string detail)
    {
        if (detail.StartsWith("ACCEPTANCE_COHORT_INFLIGHT ", StringComparison.Ordinal))
            return Tokens(detail).Contains("outcome=inflight", StringComparer.Ordinal);
        if (!detail.StartsWith("LOOP_RELAUNCH_DRAIN ", StringComparison.Ordinal)) return false;
        var field = Tokens(detail).FirstOrDefault(token => token.StartsWith("liveGates=", StringComparison.Ordinal));
        return field is not null && int.TryParse(field["liveGates=".Length..], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var liveGates) && liveGates > 0;
    }

    private static string[] Tokens(string detail) => detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
