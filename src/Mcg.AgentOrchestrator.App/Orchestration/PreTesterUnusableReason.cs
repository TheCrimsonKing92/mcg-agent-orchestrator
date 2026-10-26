namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PreTesterUnusableReason(string Check, IReadOnlyList<string> MissingClasses)
{
    internal static string AppendTo(PreTesterUnusableReason? reason, string line)
    {
        if (reason is null) return line;
        var result = line + "; unusable_check=" + reason.Check;
        return reason.MissingClasses.Count == 0 ? result :
            result + "; unusable_classes=" + string.Join(',', reason.MissingClasses.Select(Uri.EscapeDataString));
    }
}
