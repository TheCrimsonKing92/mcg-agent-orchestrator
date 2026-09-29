using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static bool IsProtectedBoundaryRegistrationFault(Exception fault)
    {
        const string pattern = @"worker-process-registration-failed:\s*pid=\d+;\s*stage=protected-process-boundary;\s*cleanup=refused-protected-process;\s*protected=\d+@\d+";
        if (Regex.IsMatch(fault.Message, pattern, RegexOptions.CultureInvariant)) return true;
        return fault.InnerException is { } inner && IsProtectedBoundaryRegistrationFault(inner);
    }
}
