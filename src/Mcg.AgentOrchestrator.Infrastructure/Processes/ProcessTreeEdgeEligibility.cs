using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum ProcessTreeEdgeDecision
{
    Eligible,
    TemporalInversion,
    IdentityUnavailable
}

internal readonly record struct ProcessTreeIdentityAnchor(int ProcessId, DateTimeOffset StartedAt);

internal sealed record ProcessTreeEdgeVerdict(
    ProcessTreeEdgeDecision Decision,
    int ParentProcessId,
    int? AnchorProcessId,
    DateTimeOffset? AnchorStartedAt,
    int ChildProcessId,
    string ChildName,
    DateTimeOffset? ChildStartedAt,
    ProcessInspectionStatus ChildStatus)
{
    internal string Format()
    {
        var (kind, reason) = Decision switch
        {
            ProcessTreeEdgeDecision.TemporalInversion =>
                ("PROCESS_EDGE_REJECTED", "child-precedes-parent"),
            ProcessTreeEdgeDecision.IdentityUnavailable =>
                ("PROCESS_EDGE_UNVERIFIED", "identity-unavailable"),
            _ => throw new InvalidOperationException(
                "An eligible process-tree edge cannot be formatted as an exclusion diagnostic.")
        };
        return $"{kind} parent={ParentProcessId} parent-anchor={AnchorProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"parent-anchor-created={FormatTime(AnchorStartedAt)} " +
            $"child={ChildProcessId} child-name={FormatName(ChildName)} child-created={FormatTime(ChildStartedAt)} " +
            $"child-status={ChildStatus} reason={reason}";
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unknown";

    private static string FormatName(string value)
    {
        const int maxLength = 80;
        var token = string.Concat((value ?? string.Empty).Select(character =>
            char.IsWhiteSpace(character) ? '_' : character));
        return token.Length <= maxLength ? token : token[..maxLength];
    }
}

internal static class ProcessTreeEdgeEligibility
{
    internal static ProcessTreeEdgeVerdict Evaluate(
        int parentProcessId,
        DateTimeOffset? parentStartedAt,
        ProcessInspectionRecord child) =>
        Evaluate(
            parentProcessId,
            parentStartedAt is { } startedAt
                ? new ProcessTreeIdentityAnchor(parentProcessId, startedAt)
                : null,
            child);

    internal static ProcessTreeEdgeVerdict Evaluate(
        int parentProcessId,
        ProcessTreeIdentityAnchor? parentAnchor,
        ProcessInspectionRecord child) =>
        Evaluate(
            parentProcessId,
            parentAnchor,
            child.ProcessId,
            child.Name,
            child.StartedAt,
            child.Status);

    internal static ProcessTreeEdgeVerdict Evaluate(
        int parentProcessId,
        ProcessTreeIdentityAnchor? parentAnchor,
        int childProcessId,
        string childName,
        DateTimeOffset? childStartedAt,
        ProcessInspectionStatus childStatus)
    {
        var decision = parentAnchor is null ||
            childStartedAt is null ||
            childStatus is ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled
                ? ProcessTreeEdgeDecision.IdentityUnavailable
                : childStartedAt.Value < parentAnchor.Value.StartedAt
                    ? ProcessTreeEdgeDecision.TemporalInversion
                    : ProcessTreeEdgeDecision.Eligible;

        return new ProcessTreeEdgeVerdict(
            decision,
            parentProcessId,
            parentAnchor?.ProcessId,
            parentAnchor?.StartedAt,
            childProcessId,
            childName,
            childStartedAt,
            childStatus);
    }
}
