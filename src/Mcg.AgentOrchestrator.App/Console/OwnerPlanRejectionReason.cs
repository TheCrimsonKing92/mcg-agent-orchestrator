namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerPlanRejectionReason
{
    internal static string? Read(string? message)
    {
        if (message is null || !message.Contains("Planner output contract failed", StringComparison.Ordinal)) return null;
        const string marker = "Stdout plan reason:";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        var reason = start < 0 ? message : message[(start + marker.Length)..];
        var end = reason.IndexOf("Retry Planner", StringComparison.Ordinal);
        if (end >= 0) reason = reason[..end];
        end = reason.IndexOf("Command:", StringComparison.Ordinal);
        if (end >= 0) reason = reason[..end];
        return OwnerHoldReason.FirstLine(reason)?.TrimEnd(' ', '.');
    }
}
