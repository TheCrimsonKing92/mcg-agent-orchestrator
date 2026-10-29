namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateMemberLines
{
    internal static IReadOnlyList<string> Lines(IReadOnlyList<OwnerConsoleGateMember> members)
    {
        if (members.Count <= 1)
            return [string.Join(" + ", members.Select(member => member.Id + (member.Title.Length == 0 ? "" : " " + member.Title)))];

        return members.Select(member => "  " + member.Id[..Math.Min(8, member.Id.Length)] +
            (member.Title.Length == 0 ? "" : "  " + member.Title)).ToArray();
    }
}
