using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Legacy owners carry no process start time. A live process reusing a dead owner's pid
// is classified Live and waits for the existing 30-minute age-based recovery path.
internal static class LegacyGoalEvidenceOwner
{
    private const string Prefix = "goal-evidence:";

    public static bool TryParse(string? owner, out string operation, out int pid, out string instance)
    {
        operation = string.Empty;
        pid = 0;
        instance = string.Empty;
        if (string.IsNullOrWhiteSpace(owner) ||
            !GoalEvidenceOperationOwner.IsGoalEvidence(owner) || GoalEvidenceOperationOwner.IsProtected(owner))
        {
            return false;
        }

        var parts = owner[Prefix.Length..].Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || parts[0] == "v1" ||
            !int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
            processId < 1 || string.IsNullOrWhiteSpace(parts[^1]))
        {
            return false;
        }

        var parsedOperation = string.Join(':', parts[..^2]);
        if (string.IsNullOrWhiteSpace(parsedOperation))
        {
            return false;
        }

        operation = parsedOperation;
        pid = processId;
        instance = parts[^1];
        return true;
    }

    public static GoalEvidenceLeaseRecoveryStatus ProbeStatus(IConductLockPidProbe probe, int pid)
    {
        try
        {
            return probe.IsRunning(pid)
                ? GoalEvidenceLeaseRecoveryStatus.Live
                : GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending;
        }
        catch
        {
            return GoalEvidenceLeaseRecoveryStatus.StateUnavailable;
        }
    }
}
