namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerHoldReason
{
    internal static string? Read(string? blocker)
    {
        if (string.IsNullOrWhiteSpace(blocker)) return null;
        var item = new OwnerConductEvent(default, "", null, blocker);
        return FirstLine(OwnerActivityNarrator.Field(item, "question") ??
            OwnerActivityNarrator.Field(item, "reason") ?? blocker);
    }

    internal static string? FirstLine(string? text) => text?.Split(['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
